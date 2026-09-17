using DotNetLab.Infrastructure.Worker;
using DotNetLab.Lab;
using Fluxor;

namespace DotNetLab.Features.Compiler;

public sealed class CompilerEffects(WorkerHost worker, IState<CompilerState> state)
{
    private CompilerApplyGenerations _generations;

    [EffectMethod]
    public async Task Handle(EnsureSdkVersionsAction _, IDispatcher dispatcher)
    {
        if (state.Value.ListLoaded)
        {
            return;
        }

        try
        {
            var versions = await worker.SendAsync(
                new WorkerInputMessage.GetSdkVersions
                {
                    Id = worker.NextMessageId(),
                });

            if (versions is not { Count: > 0 })
            {
                return;
            }

            dispatcher.Dispatch(new SdkVersionsLoadedAction(
                versions
                    .Select(static version => new SdkOption(
                        version.Version,
                        string.IsNullOrEmpty(version.ReleaseDate)
                            ? version.Version
                            : $"{version.Version} — {version.ReleaseDate}",
                        "",
                        ""))
                    .ToArray()));
        }
        catch
        {
            // Keep the built-in catalog if the SDK list cannot be downloaded.
        }
    }

    [EffectMethod]
    public Task Handle(ApplySdkAction action, IDispatcher dispatcher)
        => ApplySdkAsync(action.Value, dispatcher);

    [EffectMethod]
    public Task Handle(SetRoslynAction action, IDispatcher dispatcher)
        => ApplyOneAsync(CompilerKind.Roslyn, action.Version, state.Value.RoslynConfig, dispatcher);

    [EffectMethod]
    public Task Handle(SetRazorAction action, IDispatcher dispatcher)
        => ApplyOneAsync(CompilerKind.Razor, action.Version, state.Value.RazorConfig, dispatcher);

    [EffectMethod]
    public Task Handle(SetRoslynConfigAction action, IDispatcher dispatcher)
        => ApplyOneAsync(CompilerKind.Roslyn, state.Value.Roslyn, action.Config, dispatcher);

    [EffectMethod]
    public Task Handle(SetRazorConfigAction action, IDispatcher dispatcher)
        => ApplyOneAsync(CompilerKind.Razor, state.Value.Razor, action.Config, dispatcher);

    [EffectMethod]
    public Task Handle(RestoreCompilersAction action, IDispatcher dispatcher)
        => ApplyBothAsync(action.Roslyn, action.RoslynConfig, action.Razor, action.RazorConfig, dispatcher);

    private async Task ApplySdkAsync(string value, IDispatcher dispatcher)
    {
        var generation = _generations.BeginSdk();
        var sdk = CompilerSpec.Display(value);
        dispatcher.Dispatch(new SdkApplyStartedAction(sdk));
        try
        {
            var current = state.Value;
            if (CompilerSpec.ToSpecifier(sdk) is null)
            {
                await ApplyBothAsync("built-in", current.RoslynConfig, "built-in", current.RazorConfig, dispatcher);
                return;
            }

            SdkInfo info;
            try
            {
                info = await worker.SendAsync(
                    new WorkerInputMessage.GetSdkInfo(sdk)
                    {
                        Id = worker.NextMessageId(),
                    });
            }
            catch (Exception ex)
            {
                if (!_generations.IsCurrentSdk(generation))
                {
                    return;
                }

                dispatcher.Dispatch(new SdkApplyFailedAction(ex.Message));
                var found = state.Value.Resolved;
                if (string.IsNullOrEmpty(found.Roslyn) && string.IsNullOrEmpty(found.Razor))
                {
                    return;
                }

                await ApplyBothAsync(
                    string.IsNullOrEmpty(found.Roslyn) ? current.Roslyn : found.Roslyn,
                    current.RoslynConfig,
                    string.IsNullOrEmpty(found.Razor) ? current.Razor : found.Razor,
                    current.RazorConfig,
                    dispatcher);
                return;
            }

            if (!_generations.IsCurrentSdk(generation))
            {
                return;
            }

            dispatcher.Dispatch(new SdkResolvedAction(CompilerSpec.Display(info.SdkVersion)));
            await ApplyBothAsync(
                info.RoslynVersion ?? "built-in",
                current.RoslynConfig,
                info.RazorVersion ?? "built-in",
                current.RazorConfig,
                dispatcher);
        }
        finally
        {
            if (_generations.IsCurrentSdk(generation))
            {
                dispatcher.Dispatch(new SdkApplyFinishedAction());
            }
        }
    }

    private Task ApplyBothAsync(
        string roslyn,
        string roslynConfig,
        string razor,
        string razorConfig,
        IDispatcher dispatcher)
        => Task.WhenAll(
            ApplyOneAsync(CompilerKind.Roslyn, roslyn, roslynConfig, dispatcher),
            ApplyOneAsync(CompilerKind.Razor, razor, razorConfig, dispatcher));

    private async Task ApplyOneAsync(
        CompilerKind kind,
        string version,
        string config,
        IDispatcher dispatcher)
    {
        var generation = _generations.Begin(kind);
        var display = CompilerSpec.Display(version);
        dispatcher.Dispatch(new CompilerApplyStartedAction(kind, display, config));
        try
        {
            var changed = await worker.SendAsync(
                new WorkerInputMessage.UseCompilerVersion(
                    kind,
                    CompilerSpec.ToSpecifier(version),
                    CompilerSpec.ToBuildConfiguration(config))
                {
                    Id = worker.NextMessageId(),
                });

            if (!_generations.IsCurrent(kind, generation))
            {
                return;
            }

            PackageDependencyInfo? info = null;
            try
            {
                info = await worker.SendAsync(
                    new WorkerInputMessage.GetCompilerDependencyInfo(kind)
                    {
                        Id = worker.NextMessageId(),
                    });
            }
            catch
            {
                // Resolved package info is optional.
            }

            if (!_generations.IsCurrent(kind, generation))
            {
                return;
            }

            dispatcher.Dispatch(new CompilerApplySucceededAction(kind, info, changed));
        }
        catch (Exception ex)
        {
            if (!_generations.IsCurrent(kind, generation))
            {
                return;
            }

            dispatcher.Dispatch(new CompilerApplyFailedAction(kind, ex.Message));
        }
        finally
        {
            if (_generations.IsCurrent(kind, generation))
            {
                dispatcher.Dispatch(new CompilerApplyFinishedAction(kind));
            }
        }
    }
}
