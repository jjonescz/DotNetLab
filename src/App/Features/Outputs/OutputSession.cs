using DotNetLab.Features.Compiler;
using DotNetLab.Lab;

namespace DotNetLab.Features.Outputs;

public sealed class OutputSession
{
    private readonly IOutputSessionHost _host;
    private readonly Dictionary<string, OutputSnapshot> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _modelUris = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    private bool _showErrorListIfOutputEmpty;

    internal OutputSession(IOutputSessionHost host)
    {
        _host = host;
    }

    public bool IsEmpty => _cache.Count == 0;

    public string DisplayType
        => _showErrorListIfOutputEmpty && HasEmptyOutputText(_host.ActiveOutput) == true
            ? LabCatalog.ErrorsOutputType
            : _host.ActiveOutput;

    public void Clear()
    {
        _cache.Clear();
        _loading.Clear();
    }

    public bool DismissTemporaryErrorList()
    {
        var wasShowing = _showErrorListIfOutputEmpty;
        _showErrorListIfOutputEmpty = false;
        return wasShowing;
    }

    public void SetTemporaryErrorList(bool show)
        => _showErrorListIfOutputEmpty = show;

    public string OutputLanguage(string type)
        => TryGetSnapshot(type, out var snapshot)
            ? snapshot.Language
            : "plaintext";

    public string OutputUriFor(string tab)
    {
        if (!_modelUris.TryGetValue(tab, out var uri))
        {
            uri = CompiledAssembly.GetOutputModelUri(OutputFileName(tab), tab);
            _modelUris[tab] = uri;
        }

        return uri;
    }

    public string GetOutput(string tab)
    {
        var key = OutputCacheKey(tab);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached.Text;
        }

        if (_host.Running)
        {
            // Keep the previous output in Monaco. Replacing it with "Compiling…" races
            // with a cached recompile and can leave that placeholder stuck until a tab switch.
            var runningOutput = FindOutput(tab);
            if (runningOutput?.Text is { } runningText)
            {
                return runningText;
            }

            return "Compiling…";
        }

        if (_host.Compiled is not { } compiled)
        {
            return "(press Compile to load this)";
        }

        if (compiled.GetGlobalOutput("fail") is { Text: { } failText })
        {
            return failText;
        }

        var output = FindOutput(tab);
        if (output is null)
        {
            return $"(no {_host.OutputLabel(tab)} output for this file)";
        }

        if (output.Text is { } eager)
        {
            _cache[key] = CreateSnapshot(tab, eager, output, output.Metadata);
            return eager;
        }

        _ = EnsureOutputLoadedAsync(tab);
        return "Loading…";
    }

    public async Task EnsureOutputLoadedAsync(string tab)
    {
        if (_host.Compiled is null || _host.LastInput is null || _host.Running)
        {
            return;
        }

        var key = OutputCacheKey(tab);
        if (_cache.ContainsKey(key) || !_loading.Add(key))
        {
            return;
        }

        var generation = _host.CompileGeneration;
        try
        {
            // LoadAsync is often already completed for a cached assembly. Yield so Notify
            // does not run in the middle of a Blazor render (GetOutput is called from one).
            await Task.Yield();
            if (!_host.IsCurrentCompile(generation) || _host.Running || _host.Compiled is null)
            {
                return;
            }

            var output = FindOutput(tab);
            if (output is null)
            {
                _cache[key] = Placeholder(tab, $"(no {_host.OutputLabel(tab)} output for this file)");
                return;
            }

            if (output.Text is { } eager)
            {
                _cache[key] = CreateSnapshot(tab, eager, output, output.Metadata);
                return;
            }

            var file = OutputFileName(tab);
            CompiledFileLazyResult result;
            try
            {
                result = await output.LoadAsync(new()
                {
                    OutputFactory = () => _host.LoadFromWorkerAsync(file, tab),
                });
            }
            catch (Exception ex)
            {
                result = new() { Text = ex.ToString(), Metadata = CompiledFileOutputMetadata.SpecialMessage };
            }

            if (!_host.IsCurrentCompile(generation))
            {
                return;
            }

            _cache[key] = CreateSnapshot(tab, result.Text, output, result.Metadata ?? output.Metadata);
            if (_host.StoreInCache && _host.Compiled is { } compiled)
            {
                _host.StoreCompiledOutput(compiled);
            }
        }
        finally
        {
            _loading.Remove(key);
            _host.Notify();
        }
    }

    public async Task LoadDisplayedAsync()
    {
        await EnsureOutputLoadedAsync(_host.ActiveOutput);
        if (!string.Equals(DisplayType, _host.ActiveOutput, StringComparison.Ordinal))
        {
            await EnsureOutputLoadedAsync(DisplayType);
        }
    }

    internal bool TryGetSnapshot(string tab, out OutputSnapshot snapshot)
        => _cache.TryGetValue(OutputCacheKey(tab), out snapshot!);

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
        if (_host.Compiled is not { } compiled)
        {
            return null;
        }

        if (compiled.Files.TryGetValue(_host.ActiveSource, out var file) &&
            file.GetOutput(tab) is { } perFile)
        {
            return perFile;
        }

        return compiled.GetGlobalOutput(tab);
    }

    private string? OutputFileName(string tab)
        => FindOutput(tab) is not null &&
           _host.Compiled?.Files.TryGetValue(_host.ActiveSource, out var file) == true &&
           file.GetOutput(tab) is not null
            ? _host.ActiveSource
            : null;

    private string OutputCacheKey(string tab) => $"{_host.ActiveSource}\0{tab}";

    private OutputSnapshot Placeholder(string tab, string text)
        => new(text, "plaintext", CompiledFileOutputMetadata.SpecialMessage, OutputUriFor(tab));

    private OutputSnapshot CreateSnapshot(
        string tab,
        string text,
        CompiledFileOutput? output,
        CompiledFileOutputMetadata? metadata)
    {
        var language = metadata is { MessageKind: not MessageKind.Normal }
            ? "plaintext"
            : output?.Language ?? LabCatalog.OutputLanguage(tab);
        return new(text, language, metadata, OutputUriFor(tab));
    }
}

internal sealed record OutputSnapshot(
    string Text,
    string Language,
    CompiledFileOutputMetadata? Metadata,
    string ModelUri);

internal interface IOutputSessionHost
{
    string ActiveSource { get; }

    string ActiveOutput { get; }

    bool Running { get; }

    CompiledAssembly? Compiled { get; }

    CompilationInput? LastInput { get; }

    bool StoreInCache { get; }

    int CompileGeneration { get; }

    bool IsCurrentCompile(int generation);

    string OutputLabel(string tab);

    void Notify();

    ValueTask<CompiledFileLazyResult> LoadFromWorkerAsync(string? file, string tab);

    void StoreCompiledOutput(CompiledAssembly compiled);
}
