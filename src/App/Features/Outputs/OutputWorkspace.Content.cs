using DotNetLab.Features.Compiler;
using DotNetLab.Infrastructure.Worker;
using DotNetLab.Lab;

namespace DotNetLab.Features.Outputs;

public sealed partial class OutputWorkspace
{
    public bool IsEmpty => _cache.Count == 0;

    public void Clear()
    {
        RememberNativeAsm();
        _cache.Clear();
        _loading.Clear();
    }

    public bool DismissTemporaryErrorList()
    {
        var wasShowing = _showErrorListIfOutputEmpty;
        _showErrorListIfOutputEmpty = false;
        if (wasShowing)
        {
            Notify();
        }

        return wasShowing;
    }

    public void SetTemporaryErrorList(bool show)
        => _showErrorListIfOutputEmpty = show;

    public string OutputLanguage(string type)
        => TryGetSnapshot(type, out var snapshot)
            ? snapshot.Language
            : "plaintext";

    public string OutputLanguage(LabOutput output) => OutputLanguage(output.Id);

    public OutputDisclaimer GetDisclaimer(string type)
        => TryGetSnapshot(type, out var snapshot)
            ? snapshot.Disclaimer
            : OutputDisclaimer.None;

    public OutputDisclaimer GetDisclaimer(LabOutput output) => GetDisclaimer(output.Id);

    public string OutputUriFor(string tab)
    {
        if (!_modelUris.TryGetValue(tab, out var uri))
        {
            uri = CompiledAssembly.GetOutputModelUri(OutputFileName(tab), tab);
            _modelUris[tab] = uri;
        }

        return uri;
    }

    public string OutputUriFor(LabOutput output) => OutputUriFor(output.Id);

    public string GetOutput(LabOutput output) => GetOutput(output.Id);

    public string GetOutput(string tab)
    {
        var key = OutputCacheKey(tab);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached.Text;
        }

        if (Running)
        {
            var runningOutput = FindOutput(tab);
            if (runningOutput?.Text is { } runningText)
            {
                return runningText;
            }

            return "Compiling…";
        }

        if (Compiled is not { } compiled)
        {
            return "(press Compile to load this)";
        }

        if (compiled.GetGlobalOutput(OutputCatalog.FailId) is { Text: { } failText })
        {
            return failText;
        }

        var output = FindOutput(tab);
        if (output is null)
        {
            return $"(no {OutputCatalog.Label(tab)} output for this file)";
        }

        if (output.Text is { } eager)
        {
            var snapshot = CreateSnapshot(tab, new CompiledFileLazyResult { Text = eager, Metadata = output.Metadata }, output);
            _cache[key] = snapshot;
            return snapshot.Text;
        }

        _ = EnsureLoadedAsync(tab);
        return "Loading…";
    }

    public Task EnsureOutputLoadedAsync(string tab) => EnsureLoadedAsync(tab);

    public async Task EnsureLoadedAsync(string tab)
    {
        if (Compiled is null || LastInput is null || Running)
        {
            return;
        }

        var key = OutputCacheKey(tab);
        if (_cache.ContainsKey(key) || !_loading.Add(key))
        {
            return;
        }

        var generation = CompileGeneration;
        try
        {
            await Task.Yield();
            if (!IsCurrentCompile(generation) || Running || Compiled is null)
            {
                return;
            }

            var output = FindOutput(tab);
            if (output is null)
            {
                _cache[key] = Placeholder(tab, $"(no {OutputCatalog.Label(tab)} output for this file)");
                return;
            }

            if (output.Text is { } eager)
            {
                _cache[key] = CreateSnapshot(tab, new CompiledFileLazyResult { Text = eager, Metadata = output.Metadata }, output);
                return;
            }

            var file = OutputFileName(tab);
            CompiledFileLazyResult result;
            try
            {
                result = await output.LoadAsync(new()
                {
                    OutputFactory = () => LoadFromWorkerAsync(file, tab),
                });
            }
            catch (Exception ex)
            {
                result = new() { Text = ex.ToString(), Metadata = CompiledFileOutputMetadata.SpecialMessage };
            }

            if (!IsCurrentCompile(generation))
            {
                return;
            }

            _cache[key] = CreateSnapshot(tab, result, output);
            if (StoreInCache && Compiled is { } compiled)
            {
                StoreCompiledOutput(compiled);
            }
        }
        finally
        {
            _loading.Remove(key);
            Notify();
        }
    }

    internal Task RefreshDisplayAsync()
    {
        SetTemporaryErrorList(Compiled is { NumErrors: > 0 });
        Notify();
        return LoadDisplayedAsync();
    }

    internal bool TryGetSnapshot(string tab, out OutputSnapshot snapshot)
        => _cache.TryGetValue(OutputCacheKey(tab), out snapshot!);

    private async Task LoadDisplayedAsync()
    {
        await EnsureLoadedAsync(ActiveOutput);
        if (!string.Equals(DisplayType, ActiveOutput, StringComparison.Ordinal))
        {
            await EnsureLoadedAsync(DisplayType);
        }
    }

    private void StoreCompiledOutput(CompiledAssembly compiled)
    {
        if (_compilationSession is not null)
        {
            _compilationSession.StoreCompiledOutput(compiled);
            return;
        }

        _compile?.Store(compiled);
    }

    private async ValueTask<CompiledFileLazyResult> LoadFromWorkerAsync(string? file, string tab)
    {
        if (_worker is null || LastInput is null)
        {
            return new CompiledFileLazyResult { Text = "" };
        }

        return await _worker.SendAsync(
            new WorkerInputMessage.GetOutput(LastInput, file, tab)
            {
                Id = _worker.NextMessageId(),
            });
    }

    private bool? HasEmptyOutputText(string tab)
    {
        var output = FindOutput(tab);
        if (output is null)
        {
            return null;
        }

        if (output.Text is { } eager)
        {
            return string.IsNullOrEmpty(eager);
        }

        if (_cache.TryGetValue(OutputCacheKey(tab), out var snapshot))
        {
            return string.IsNullOrEmpty(snapshot.Text);
        }

        return null;
    }

    private CompiledFileOutput? FindOutput(string tab)
    {
        if (Compiled is not { } compiled)
        {
            return null;
        }

        if (compiled.Files.TryGetValue(ActiveSource, out var file) &&
            file.GetOutput(tab) is { } perFile)
        {
            return perFile;
        }

        return compiled.GetGlobalOutput(tab);
    }

    private string? OutputFileName(string tab)
        => FindOutput(tab) is not null &&
           Compiled?.Files.TryGetValue(ActiveSource, out var file) == true &&
           file.GetOutput(tab) is not null
            ? ActiveSource
            : null;

    private string OutputCacheKey(string tab) => $"{ActiveSource}\0{tab}";

    private OutputSnapshot Placeholder(string tab, string text)
        => new(text, "plaintext", CompiledFileOutputMetadata.SpecialMessage, OutputDisclaimer.None, OutputUriFor(tab));

    private OutputSnapshot CreateSnapshot(
        string tab,
        CompiledFileLazyResult result,
        CompiledFileOutput? output)
    {
        var metadata = result.Metadata ?? output?.Metadata;
        string? language = metadata is { MessageKind: not MessageKind.Normal }
            ? "plaintext"
            : output?.Language ?? OutputCatalog.Language(tab);
        _cache.TryGetValue(OutputCacheKey(tab), out var previous);
        var info = output is null
            ? (OutputInfo?)null
            : new OutputInfo
            {
                Output = output,
                File = OutputFileName(tab),
                CachedOutput = CachedOutputFor(tab, previous),
            };
        var text = _plugin.GetText(info, result with { Metadata = metadata }, out var disclaimer, ref language);
        if (string.IsNullOrEmpty(language))
        {
            language = "plaintext";
        }

        return new(text, language, metadata, disclaimer, OutputUriFor(tab));
    }

    private void RememberNativeAsm()
    {
        foreach (var snapshot in _cache.Values)
        {
            if (snapshot.Disclaimer == OutputDisclaimer.None &&
                string.Equals(snapshot.Language, "x86", StringComparison.Ordinal))
            {
                _cachedNativeAsm = snapshot;
                return;
            }
        }
    }

    private CompiledFileOutput? CachedOutputFor(string tab, OutputSnapshot? previous)
    {
        var cached = previous ?? (string.Equals(tab, OutputCatalog.Asm.Id, StringComparison.Ordinal) ? _cachedNativeAsm : null);
        if (cached is null)
        {
            return null;
        }

        return new CompiledFileOutput
        {
            Type = tab,
            Label = OutputCatalog.Label(tab),
            Language = cached.Language,
            EagerText = cached.Text,
        };
    }
}

internal sealed record OutputSnapshot(
    string Text,
    string Language,
    CompiledFileOutputMetadata? Metadata,
    OutputDisclaimer Disclaimer,
    string ModelUri);

internal sealed class OutputCompileState
{
    public CompiledAssembly? Compiled { get; set; }

    public CompilationInput? LastInput { get; set; }

    public int CompileGeneration { get; set; }

    public bool StoreInCache { get; set; }

    public int StoredCount { get; private set; }

    public void Store(CompiledAssembly compiled)
    {
        StoredCount++;
        _ = compiled;
    }
}
