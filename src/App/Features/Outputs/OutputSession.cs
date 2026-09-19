using DotNetLab.Features.Compilation;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Infrastructure.Worker;
using DotNetLab.Lab;
using Fluxor;

namespace DotNetLab.Features.Outputs;

public sealed class OutputSession
{
    private readonly LabDocuments _documents;
    private readonly IState<OutputState> _output;
    private readonly IState<CompilationState> _compilation;
    private readonly ICompilerOutputPlugin _plugin;
    private readonly Lazy<CompilationSession>? _compilationSession;
    private readonly Lazy<OutputTabLayout>? _tabs;
    private readonly WorkerHost? _worker;
    private readonly OutputCompileState? _compile;
    private readonly Dictionary<string, OutputSnapshot> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _modelUris = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    private OutputSnapshot? _cachedNativeAsm;
    private bool _showErrorListIfOutputEmpty;

    internal OutputSession(
        LabDocuments documents,
        IState<OutputState> output,
        IState<CompilationState> compilation,
        ICompilerOutputPlugin? plugin = null,
        Lazy<CompilationSession>? compilationSession = null,
        Lazy<OutputTabLayout>? tabs = null,
        WorkerHost? worker = null,
        OutputCompileState? compile = null)
    {
        _documents = documents;
        _output = output;
        _compilation = compilation;
        _plugin = plugin ?? PassThroughCompilerOutputPlugin.Instance;
        _compilationSession = compilationSession;
        _tabs = tabs;
        _worker = worker;
        _compile = compile;
    }

    public event Action? Changed;

    public bool IsEmpty => _cache.Count == 0;

    public string DisplayType
        => _showErrorListIfOutputEmpty && HasEmptyOutputText(ActiveOutput) == true
            ? LabCatalog.ErrorsOutputType
            : ActiveOutput;

    private string ActiveSource => _documents.ActiveSource;

    private string ActiveOutput => _output.Value.ActiveOutput;

    private bool Running => _compilation.Value.Running;

    private CompiledAssembly? Compiled => _compilationSession?.Value.Compiled ?? _compile?.Compiled;

    private CompilationInput? LastInput => _compilationSession?.Value.LastInput ?? _compile?.LastInput;

    private bool StoreInCache => _compilationSession?.Value.StoreInCache ?? _compile?.StoreInCache ?? false;

    private int CompileGeneration => _compilationSession?.Value.CompileGeneration ?? _compile?.CompileGeneration ?? 0;

    private bool IsCurrentCompile(int generation)
        => _compilationSession?.Value.IsCurrentCompile(generation)
            ?? generation == (_compile?.CompileGeneration ?? 0);

    private string OutputLabel(string tab)
        => _tabs?.Value.OutputLabel(tab) ?? LabCatalog.OutputTypeLabel(tab);

    private void Notify() => Changed?.Invoke();

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
        return wasShowing;
    }

    public void SetTemporaryErrorList(bool show)
        => _showErrorListIfOutputEmpty = show;

    public string OutputLanguage(string type)
        => TryGetSnapshot(type, out var snapshot)
            ? snapshot.Language
            : "plaintext";

    public OutputDisclaimer GetDisclaimer(string type)
        => TryGetSnapshot(type, out var snapshot)
            ? snapshot.Disclaimer
            : OutputDisclaimer.None;

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

        if (Running)
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

        if (Compiled is not { } compiled)
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
            return $"(no {OutputLabel(tab)} output for this file)";
        }

        if (output.Text is { } eager)
        {
            var snapshot = CreateSnapshot(tab, new CompiledFileLazyResult { Text = eager, Metadata = output.Metadata }, output);
            _cache[key] = snapshot;
            return snapshot.Text;
        }

        _ = EnsureOutputLoadedAsync(tab);
        return "Loading…";
    }

    public async Task EnsureOutputLoadedAsync(string tab)
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
            // LoadAsync is often already completed for a cached assembly. Yield so Notify
            // does not run in the middle of a Blazor render (GetOutput is called from one).
            await Task.Yield();
            if (!IsCurrentCompile(generation) || Running || Compiled is null)
            {
                return;
            }

            var output = FindOutput(tab);
            if (output is null)
            {
                _cache[key] = Placeholder(tab, $"(no {OutputLabel(tab)} output for this file)");
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

    public async Task LoadDisplayedAsync()
    {
        await EnsureOutputLoadedAsync(ActiveOutput);
        if (!string.Equals(DisplayType, ActiveOutput, StringComparison.Ordinal))
        {
            await EnsureOutputLoadedAsync(DisplayType);
        }
    }

    internal bool TryGetSnapshot(string tab, out OutputSnapshot snapshot)
        => _cache.TryGetValue(OutputCacheKey(tab), out snapshot!);

    private void StoreCompiledOutput(CompiledAssembly compiled)
    {
        if (_compilationSession is not null)
        {
            _compilationSession.Value.StoreCompiledOutput(compiled);
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
            : output?.Language ?? LabCatalog.OutputLanguage(tab);
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
        var cached = previous ?? (string.Equals(tab, "asm", StringComparison.Ordinal) ? _cachedNativeAsm : null);
        if (cached is null)
        {
            return null;
        }

        return new CompiledFileOutput
        {
            Type = tab,
            Label = OutputLabel(tab),
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
