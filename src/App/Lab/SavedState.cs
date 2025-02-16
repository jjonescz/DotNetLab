using BlazorMonaco.Editor;
using ProtoBuf;

namespace DotNetLab.Lab;

partial class Page
{
    private SavedState savedState = SavedState.Initial;

    private string GetCurrentSlug()
    {
        var uri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
        return uri.Fragment.TrimStart('#');
    }

    private async Task LoadStateFromUrlAsync()
    {
        var slug = GetCurrentSlug();

        savedState = slug switch
        {
            _ when string.IsNullOrWhiteSpace(slug) => SavedState.Initial,
            "csharp" => InitialCode.CSharp.ToSavedState(),
            "razor" => SavedState.Initial,
            "cshtml" => InitialCode.Cshtml.ToSavedState(),
            _ => Compressor.Uncompress(slug),
        };

        // Load inputs.
        inputs.Clear();
        activeInputTabId = IndexToInputTabId(0);
        TextModel? firstModel = null;
        foreach (var (index, input) in savedState.Inputs.Index())
        {
            var model = await CreateModelAsync(input);
            inputs.Add(new(input.FileName, model) { NewContent = input.Text });

            if (index == 0)
            {
                firstModel = model;
            }
        }

        if (savedState.Configuration is { } savedConfiguration)
        {
            var input = InitialCode.Configuration.ToInputCode() with { Text = savedConfiguration };
            configuration = new(input.FileName, await CreateModelAsync(input));
        }

        OnWorkspaceChanged();

        if (firstModel != null)
        {
            await inputEditor.SetModel(firstModel);
        }

        // Load settings.
        await settings.LoadFromStateAsync(savedState);
    }

    internal async Task<SavedState> SaveStateToUrlAsync(Func<SavedState, SavedState>? updater = null)
    {
        // Always save the current editor texts.
        var inputsToSave = await getInputsAsync();
        var configurationToSave = configuration is null ? null : await getInputAsync(configuration.Model);

        using (Util.EnsureSync())
        {
            savedState = savedState with
            {
                Inputs = inputsToSave,
                Configuration = configurationToSave,
            };

            if (updater != null)
            {
                savedState = updater(savedState);
            }
        }

        var newSlug = Compressor.Compress(savedState);
        if (newSlug != GetCurrentSlug())
        {
            NavigationManager.NavigateTo(NavigationManager.BaseUri + "#" + newSlug, forceLoad: false);
        }

        return savedState;

        async Task<ImmutableArray<InputCode>> getInputsAsync()
        {
            var inputsSnapshot = inputs.ToArray(); // to avoid modifications during enumeration
            var builder = ImmutableArray.CreateBuilder<InputCode>(inputsSnapshot.Length);
            foreach (var (fileName, model) in inputsSnapshot)
            {
                var text = await getInputAsync(model);
                builder.Add(new() { FileName = fileName, Text = text });
            }
            return builder.ToImmutable();
        }

        static async Task<string> getInputAsync(TextModel model)
        {
            return await model.GetValue(EndOfLinePreference.TextDefined, preserveBOM: true);
        }
    }
}

[ProtoContract]
internal sealed record SavedState
{
    public static SavedState Initial { get; } = new() { Inputs = [InitialCode.Razor.ToInputCode(), InitialCode.RazorImports.ToInputCode()] };

    [ProtoMember(1)]
    public ImmutableArray<InputCode> Inputs { get; init; }

    [ProtoMember(5)]
    public string? Configuration { get; init; }

    [ProtoMember(4)]
    public string? SdkVersion { get; init; }

    [ProtoMember(2)]
    public string? RoslynVersion { get; init; }

    [ProtoMember(6)]
    public BuildConfiguration RoslynConfiguration { get; init; }

    [ProtoMember(3)]
    public string? RazorVersion { get; init; }

    [ProtoMember(7)]
    public BuildConfiguration RazorConfiguration { get; init; }

    public CompilationInput ToCompilationInput()
    {
        return new(Inputs)
        {
            Configuration = Configuration,
        };
    }
}
