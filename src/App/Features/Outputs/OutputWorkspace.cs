using System.Text;
using System.Text.Json;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Infrastructure.Worker;
using DotNetLab.Lab;
using Fluxor;

namespace DotNetLab.Features.Outputs;

public sealed class OutputWorkspace
{
    private readonly LabDocuments _documents;
    private readonly IState<OutputState> _output;
    private readonly IState<CompilationState> _compilation;
    private readonly IDispatcher _dispatcher;
    private readonly ICompilerOutputPlugin _plugin;
    private readonly CompilationSession? _compilationSession;
    private readonly WorkerHost? _worker;
    private readonly OutputCompileState? _compile;
    private readonly Dictionary<string, OutputSnapshot> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _modelUris = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    private readonly Dictionary<OutputFileKind, List<string>> _outputTabOrder = CreateDefaultOutputTabOrder();
    private readonly Dictionary<OutputFileKind, HashSet<string>> _hiddenOutputTabs = CreateDefaultHiddenOutputTabs();
    private readonly Dictionary<OutputFileKind, List<string>> _openOutputTabs = new();
    private OutputSnapshot? _cachedNativeAsm;
    private OutputFileKind? _syncedOutputKind;
    private bool _showErrorListIfOutputEmpty;

    public OutputWorkspace(
        LabDocuments documents,
        IState<OutputState> output,
        IState<CompilationState> compilation,
        ICompilerOutputPlugin plugin,
        CompilationSession compilationSession,
        WorkerHost worker,
        IDispatcher dispatcher)
        : this(documents, output, compilation, dispatcher, plugin, compilationSession, worker, compile: null)
    {
    }

    internal OutputWorkspace(
        LabDocuments documents,
        IState<OutputState> output,
        IState<CompilationState> compilation,
        IDispatcher dispatcher,
        ICompilerOutputPlugin? plugin = null,
        CompilationSession? compilationSession = null,
        WorkerHost? worker = null,
        OutputCompileState? compile = null)
    {
        _documents = documents;
        _output = output;
        _compilation = compilation;
        _dispatcher = dispatcher;
        _plugin = plugin ?? PassThroughCompilerOutputPlugin.Instance;
        _compilationSession = compilationSession;
        _worker = worker;
        _compile = compile;
        documents.Changed += EnsureActiveOutput;
        if (compilationSession is not null)
        {
            compilationSession.NewOutputGeneration += Clear;
        }
    }

    public event Action? Changed;

    public int Revision { get; private set; }

    public bool IsEmpty => _cache.Count == 0;

    public bool ShowRenderedHtml { get; private set; }

    public string DisplayType
        => _showErrorListIfOutputEmpty && HasEmptyOutputText(ActiveOutput) == true
            ? OutputCatalog.ErrorsId
            : ActiveOutput;

    public IReadOnlyList<string> OpenIds
    {
        get
        {
            var produced = OutputCatalog.ProducedTypes(ActiveSource);
            return OpenTabs(OutputCatalog.KindFor(ActiveSource))
                .Where(produced.Contains)
                .ToArray();
        }
    }

    private string ActiveSource => _documents.ActiveSource;

    private string ActiveOutput => _output.Value.ActiveOutput;

    private bool Running => _compilation.Value.Running;

    private CompiledAssembly? Compiled => _compilationSession?.Compiled ?? _compile?.Compiled;

    private CompilationInput? LastInput => _compilationSession?.LastInput ?? _compile?.LastInput;

    private bool StoreInCache => _compilationSession?.StoreInCache ?? _compile?.StoreInCache ?? false;

    private int CompileGeneration => _compilationSession?.CompileGeneration ?? _compile?.CompileGeneration ?? 0;

    private bool IsCurrentCompile(int generation)
        => _compilationSession?.IsCurrentCompile(generation)
            ?? generation == (_compile?.CompileGeneration ?? 0);

    private void Notify() => Changed?.Invoke();

    public LabOutput Get(string id) => OutputCatalog.Require(id);

    public IReadOnlyList<LabOutput> SettingsRowsFor(OutputFileKind kind)
    {
        var catalog = OutputCatalog.CatalogFor(kind);
        var byId = catalog.ToDictionary(output => output.Id, StringComparer.Ordinal);
        var rows = new List<LabOutput>(catalog.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in OutputTabOrder(kind))
        {
            if (byId.TryGetValue(id, out var output) && seen.Add(id))
            {
                rows.Add(output);
            }
        }

        foreach (var output in catalog)
        {
            if (seen.Add(output.Id))
            {
                rows.Add(output);
            }
        }

        return rows;
    }

    public bool IsOutputTabVisible(OutputFileKind kind, string type)
        => !HiddenOutputTabs(kind).Contains(type);

    public bool CanMoveOutputTab(OutputFileKind kind, string type, int delta)
    {
        var order = OutputTabOrder(kind);
        var index = order.IndexOf(type);
        var next = index + delta;
        return index >= 0 && next >= 0 && next < order.Count;
    }

    public void SetOutputTabVisible(OutputFileKind kind, string type, bool visible)
    {
        if (OutputCatalog.IsLocked(type) && !visible)
        {
            return;
        }

        if (!OutputCatalog.CatalogFor(kind).Any(output => output.Id == type))
        {
            return;
        }

        var hidden = HiddenOutputTabs(kind);
        var currentlyVisible = !hidden.Contains(type);
        if (visible == currentlyVisible)
        {
            return;
        }

        if (visible)
        {
            hidden.Remove(type);
        }
        else
        {
            hidden.Add(type);
        }

        if (_openOutputTabs.TryGetValue(kind, out var tabs))
        {
            if (visible)
            {
                if (!tabs.Contains(type))
                {
                    tabs.Add(type);
                }
            }
            else
            {
                tabs.Remove(type);
            }
        }

        EnsureActiveOutput();
        Notify();
    }

    public void MoveOutputTab(OutputFileKind kind, string type, int delta)
    {
        if (!CanMoveOutputTab(kind, type, delta))
        {
            return;
        }

        var order = OutputTabOrder(kind);
        var index = order.IndexOf(type);
        var next = index + delta;
        (order[index], order[next]) = (order[next], order[index]);
        Notify();
    }

    public void ResetOutputTabs(OutputFileKind kind)
    {
        _outputTabOrder[kind] = OutputCatalog.DefaultOrder(kind);
        _hiddenOutputTabs[kind] = new HashSet<string>(StringComparer.Ordinal);
        _openOutputTabs.Remove(kind);
        if (kind == OutputCatalog.KindFor(ActiveSource))
        {
            Revision++;
        }

        EnsureActiveOutput();
        Notify();
    }

    public string SerializeOutputTabs()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteSavedKind(writer, "cs", OutputFileKind.Cs);
            WriteSavedKind(writer, "razor", OutputFileKind.Razor);
            WriteSavedKind(writer, "cshtml", OutputFileKind.Cshtml);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void ApplySavedOutputTabs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            ApplySavedKind(document.RootElement, "cs", OutputFileKind.Cs);
            ApplySavedKind(document.RootElement, "razor", OutputFileKind.Razor);
            ApplySavedKind(document.RootElement, "cshtml", OutputFileKind.Cshtml);
        }
        catch (JsonException)
        {
            return;
        }

        _openOutputTabs.Clear();
        Revision++;
        EnsureActiveOutput();
        Notify();
    }

    public void CaptureOpenOutputTabs(IReadOnlyList<string> ids)
    {
        var kind = OutputCatalog.KindFor(ActiveSource);
        var produced = OutputCatalog.ProducedTypes(ActiveSource);
        var catalog = OutputCatalog.CatalogFor(kind).Select(output => output.Id).ToHashSet(StringComparer.Ordinal);
        var next = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if ((produced.Contains(id) || catalog.Contains(id)) && seen.Add(id))
            {
                next.Add(id);
            }
        }

        if (produced.Contains(OutputCatalog.ErrorsId) && seen.Add(OutputCatalog.ErrorsId))
        {
            next.Add(OutputCatalog.ErrorsId);
        }

        _openOutputTabs[kind] = next;
    }

    public IReadOnlyList<LabOutput> AddableOutputTabsFor(IReadOnlyList<string> open)
    {
        var openSet = open.ToHashSet(StringComparer.Ordinal);
        var produced = OutputCatalog.ProducedTypes(ActiveSource);
        return OutputCatalog.CatalogFor(OutputCatalog.KindFor(ActiveSource))
            .Where(output => produced.Contains(output.Id) && !openSet.Contains(output.Id))
            .ToArray();
    }

    public bool HasClosedOutputTabs(IReadOnlyList<string> open)
    {
        var openSet = open.ToHashSet(StringComparer.Ordinal);
        return VisibleTabsFor(ActiveSource).Any(output => !openSet.Contains(output.Id));
    }

    public bool OutputTabOrderDiffers(IReadOnlyList<string> open)
    {
        var order = OutputTabOrder(OutputCatalog.KindFor(ActiveSource));
        var expected = order.Where(open.Contains).ToList();
        var current = open.Where(order.Contains).ToList();
        return !expected.SequenceEqual(current, StringComparer.Ordinal);
    }

    public void AddOutputTab(string type)
    {
        var kind = OutputCatalog.KindFor(ActiveSource);
        var produced = OutputCatalog.ProducedTypes(ActiveSource);
        if (!produced.Contains(type))
        {
            return;
        }

        var tabs = OpenTabs(kind);
        if (tabs.Contains(type))
        {
            return;
        }

        tabs.Add(type);
        SetActiveOutput(type);
        Notify();
    }

    public void RestoreOutputTabOrder()
    {
        var kind = OutputCatalog.KindFor(ActiveSource);
        var tabs = OpenTabs(kind);
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = OutputTabOrder(kind);
        for (var i = 0; i < order.Count; i++)
        {
            rank[order[i]] = i;
        }

        _openOutputTabs[kind] = tabs
            .OrderBy(id => rank.GetValueOrDefault(id, int.MaxValue))
            .ThenBy(id => tabs.IndexOf(id))
            .ToList();
        Revision++;
        Notify();
    }

    public void RestoreClosedOutputTabs()
    {
        var kind = OutputCatalog.KindFor(ActiveSource);
        var tabs = OpenTabs(kind);
        var settingsOrder = OutputTabOrder(kind);
        foreach (var output in VisibleTabsFor(ActiveSource))
        {
            if (tabs.Contains(output.Id))
            {
                continue;
            }

            var idRank = settingsOrder.IndexOf(output.Id);
            var insertAt = tabs.Count;
            for (var i = 0; i < tabs.Count; i++)
            {
                var rank = settingsOrder.IndexOf(tabs[i]);
                if (rank >= 0 && rank > idRank)
                {
                    insertAt = i;
                    break;
                }
            }

            tabs.Insert(insertAt, output.Id);
        }

        Revision++;
        EnsureActiveOutput();
        Notify();
    }

    public void SaveOpenOutputTabsAsSettings()
    {
        var kind = OutputCatalog.KindFor(ActiveSource);
        var catalog = OutputCatalog.CatalogFor(kind);
        var catalogIds = catalog.Select(output => output.Id).ToHashSet(StringComparer.Ordinal);
        var open = OpenTabs(kind)
            .Where(catalogIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!open.Contains(OutputCatalog.ErrorsId))
        {
            open.Add(OutputCatalog.ErrorsId);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var id in open)
        {
            if (seen.Add(id))
            {
                order.Add(id);
            }
        }

        foreach (var id in OutputTabOrder(kind))
        {
            if (catalogIds.Contains(id) && seen.Add(id))
            {
                order.Add(id);
            }
        }

        foreach (var output in catalog)
        {
            if (seen.Add(output.Id))
            {
                order.Add(output.Id);
            }
        }

        _outputTabOrder[kind] = order;
        _hiddenOutputTabs[kind] = catalog
            .Select(output => output.Id)
            .Where(id => !open.Contains(id) && id != OutputCatalog.ErrorsId)
            .ToHashSet(StringComparer.Ordinal);
        Notify();
    }

    public void EnsureActiveOutput()
    {
        SyncOpenOutputKind();
        var tabs = OpenIds;
        if (tabs.Contains(ActiveOutput))
        {
            return;
        }

        SetActiveOutput(tabs.FirstOrDefault(id => id is "cs" or "gcs")
            ?? tabs.FirstOrDefault()
            ?? OutputCatalog.ErrorsId);
    }

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

    public void SetShowRenderedHtml(bool value)
    {
        if (ShowRenderedHtml == value)
        {
            return;
        }

        ShowRenderedHtml = value;
        Notify();
    }

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

    private IReadOnlyList<LabOutput> VisibleTabsFor(string fileName)
    {
        var kind = OutputCatalog.KindFor(fileName);
        var catalog = OutputCatalog.CatalogFor(kind);
        var byId = catalog.ToDictionary(output => output.Id, StringComparer.Ordinal);
        var produced = OutputCatalog.ProducedTypes(fileName);
        var tabs = new List<LabOutput>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in OutputTabOrder(kind))
        {
            if (HiddenOutputTabs(kind).Contains(id) || !produced.Contains(id) || !seen.Add(id))
            {
                continue;
            }

            if (byId.TryGetValue(id, out var output))
            {
                tabs.Add(output);
            }
        }

        foreach (var id in produced)
        {
            if (!byId.ContainsKey(id) && seen.Add(id))
            {
                tabs.Add(OutputCatalog.Require(id));
            }
        }

        return tabs;
    }

    private List<string> OpenTabs(OutputFileKind kind)
    {
        if (!_openOutputTabs.TryGetValue(kind, out var tabs))
        {
            var fileName = kind == OutputCatalog.KindFor(ActiveSource)
                ? ActiveSource
                : OutputCatalog.RepresentativeFile(kind);
            tabs = VisibleTabsFor(fileName).Select(output => output.Id).ToList();
            _openOutputTabs[kind] = tabs;
        }

        return tabs;
    }

    private void SyncOpenOutputKind()
    {
        var kind = OutputCatalog.KindFor(ActiveSource);
        var tabs = OpenTabs(kind);
        var produced = OutputCatalog.ProducedTypes(ActiveSource);
        tabs.RemoveAll(id => !produced.Contains(id));
        if (produced.Contains(OutputCatalog.ErrorsId) && !tabs.Contains(OutputCatalog.ErrorsId))
        {
            tabs.Add(OutputCatalog.ErrorsId);
        }

        if (_syncedOutputKind != kind)
        {
            var first = _syncedOutputKind is null;
            _syncedOutputKind = kind;
            if (!first)
            {
                Revision++;
            }
        }
    }

    private List<string> OutputTabOrder(OutputFileKind kind) => _outputTabOrder[kind];

    private HashSet<string> HiddenOutputTabs(OutputFileKind kind) => _hiddenOutputTabs[kind];

    private static Dictionary<OutputFileKind, List<string>> CreateDefaultOutputTabOrder()
        => new()
        {
            [OutputFileKind.Cs] = OutputCatalog.DefaultOrder(OutputFileKind.Cs),
            [OutputFileKind.Razor] = OutputCatalog.DefaultOrder(OutputFileKind.Razor),
            [OutputFileKind.Cshtml] = OutputCatalog.DefaultOrder(OutputFileKind.Cshtml)
        };

    private static Dictionary<OutputFileKind, HashSet<string>> CreateDefaultHiddenOutputTabs()
        => new()
        {
            [OutputFileKind.Cs] = new(StringComparer.Ordinal),
            [OutputFileKind.Razor] = new(StringComparer.Ordinal),
            [OutputFileKind.Cshtml] = new(StringComparer.Ordinal)
        };

    private void WriteSavedKind(Utf8JsonWriter writer, string property, OutputFileKind kind)
    {
        writer.WritePropertyName(property);
        writer.WriteStartObject();
        writer.WriteStartArray("order");
        foreach (var id in OutputTabOrder(kind))
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("hidden");
        foreach (var id in HiddenOutputTabs(kind))
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private void ApplySavedKind(JsonElement root, string property, OutputFileKind kind)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            ResetKindToDefault(kind);
            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            ApplyLegacyVisibleIds(kind, ReadStringArray(value));
            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            ResetKindToDefault(kind);
            return;
        }

        var catalog = OutputCatalog.CatalogFor(kind).Select(output => output.Id).ToHashSet(StringComparer.Ordinal);
        var order = value.TryGetProperty("order", out var orderElement) && orderElement.ValueKind == JsonValueKind.Array
            ? SanitizeOrder(kind, ReadStringArray(orderElement))
            : OutputCatalog.DefaultOrder(kind);
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (value.TryGetProperty("hidden", out var hiddenElement) && hiddenElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in ReadStringArray(hiddenElement))
            {
                if (catalog.Contains(id) && id != OutputCatalog.ErrorsId)
                {
                    hidden.Add(id);
                }
            }
        }

        _outputTabOrder[kind] = order;
        _hiddenOutputTabs[kind] = hidden;
    }

    private void ApplyLegacyVisibleIds(OutputFileKind kind, IReadOnlyList<string> visible)
    {
        var catalog = OutputCatalog.CatalogFor(kind);
        var catalogIds = catalog.Select(output => output.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        var hidden = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in visible)
        {
            if (catalogIds.Contains(id) && seen.Add(id))
            {
                order.Add(id);
            }
        }

        foreach (var output in catalog)
        {
            if (seen.Add(output.Id))
            {
                order.Add(output.Id);
                if (output.Id != OutputCatalog.ErrorsId)
                {
                    hidden.Add(output.Id);
                }
            }
        }

        if (!order.Contains(OutputCatalog.ErrorsId))
        {
            order.Add(OutputCatalog.ErrorsId);
        }

        hidden.Remove(OutputCatalog.ErrorsId);
        _outputTabOrder[kind] = order;
        _hiddenOutputTabs[kind] = hidden;
    }

    private static List<string> SanitizeOrder(OutputFileKind kind, IReadOnlyList<string> ids)
    {
        var catalog = OutputCatalog.CatalogFor(kind);
        var catalogIds = catalog.Select(output => output.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var id in ids)
        {
            if (catalogIds.Contains(id) && seen.Add(id))
            {
                order.Add(id);
            }
        }

        foreach (var output in catalog)
        {
            if (seen.Add(output.Id))
            {
                order.Add(output.Id);
            }
        }

        return order;
    }

    private static List<string> ReadStringArray(JsonElement value)
    {
        var ids = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = item.GetString();
            if (!string.IsNullOrEmpty(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private void ResetKindToDefault(OutputFileKind kind)
    {
        _outputTabOrder[kind] = OutputCatalog.DefaultOrder(kind);
        _hiddenOutputTabs[kind] = new HashSet<string>(StringComparer.Ordinal);
    }

    private void SetActiveOutput(string type)
        => _dispatcher.Dispatch(new SetActiveOutputAction(type));

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
