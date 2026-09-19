using System.Text;
using System.Text.Json;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using Fluxor;

namespace DotNetLab.Features.Outputs;

public sealed class OutputTabLayout
{
    private readonly LabDocuments _documents;
    private readonly IState<OutputState> _output;
    private readonly IDispatcher _dispatcher;
    private readonly Dictionary<OutputFileKind, List<string>> _outputTabOrder = CreateDefaultOutputTabOrder();
    private readonly Dictionary<OutputFileKind, HashSet<string>> _hiddenOutputTabs = CreateDefaultHiddenOutputTabs();
    private readonly Dictionary<OutputFileKind, List<string>> _openOutputTabs = new();
    private OutputFileKind? _syncedOutputKind;

    public OutputTabLayout(
        LabDocuments documents,
        IState<OutputState> output,
        IDispatcher dispatcher)
    {
        _documents = documents;
        _output = output;
        _dispatcher = dispatcher;
        documents.Changed += EnsureActiveOutput;
    }

    public event Action? Changed;

    public int Revision { get; private set; }

    private string ActiveSource => _documents.ActiveSource;

    private string ActiveOutput => _output.Value.ActiveOutput;

    public IReadOnlyList<string> CurrentOutputTabIds
    {
        get
        {
            var produced = LabCatalog.ProducedOutputTypes(ActiveSource);
            return OpenTabs(LabCatalog.OutputKindFor(ActiveSource))
                .Where(produced.Contains)
                .ToArray();
        }
    }

    public IReadOnlyList<OutputTab> CurrentOutputTabs
        => CurrentOutputTabIds.Select(id => new OutputTab(id, LabCatalog.OutputTypeLabel(id))).ToArray();
    public IReadOnlyList<OutputTab> SettingsRowsFor(OutputFileKind kind)
    {
        var catalog = LabCatalog.CatalogFor(kind);
        var byType = catalog.ToDictionary(tab => tab.Type, StringComparer.Ordinal);
        var rows = new List<OutputTab>(catalog.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in OutputTabOrder(kind))
        {
            if (byType.TryGetValue(id, out var tab) && seen.Add(id))
            {
                rows.Add(tab);
            }
        }

        foreach (var tab in catalog)
        {
            if (seen.Add(tab.Type))
            {
                rows.Add(tab);
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
        if (LabCatalog.IsOutputTabLocked(type) && !visible)
        {
            return;
        }

        if (!LabCatalog.CatalogFor(kind).Any(tab => tab.Type == type))
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
        _outputTabOrder[kind] = LabCatalog.DefaultTabOrder(kind);
        _hiddenOutputTabs[kind] = new HashSet<string>(StringComparer.Ordinal);
        _openOutputTabs.Remove(kind);
        if (kind == LabCatalog.OutputKindFor(ActiveSource))
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
        var kind = LabCatalog.OutputKindFor(ActiveSource);
        var produced = LabCatalog.ProducedOutputTypes(ActiveSource);
        var catalog = LabCatalog.CatalogFor(kind).Select(tab => tab.Type).ToHashSet(StringComparer.Ordinal);
        var next = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if ((produced.Contains(id) || catalog.Contains(id)) && seen.Add(id))
            {
                next.Add(id);
            }
        }

        if (produced.Contains(LabCatalog.ErrorsOutputType) && seen.Add(LabCatalog.ErrorsOutputType))
        {
            next.Add(LabCatalog.ErrorsOutputType);
        }

        _openOutputTabs[kind] = next;
    }

    public IReadOnlyList<OutputTab> AddableOutputTabsFor(IReadOnlyList<string> open)
    {
        var openSet = open.ToHashSet(StringComparer.Ordinal);
        var produced = LabCatalog.ProducedOutputTypes(ActiveSource);
        return LabCatalog.CatalogFor(LabCatalog.OutputKindFor(ActiveSource))
            .Where(tab => produced.Contains(tab.Type) && !openSet.Contains(tab.Type))
            .ToArray();
    }

    public bool HasClosedOutputTabs(IReadOnlyList<string> open)
    {
        var openSet = open.ToHashSet(StringComparer.Ordinal);
        return OutputTabsFor(ActiveSource).Any(tab => !openSet.Contains(tab.Type));
    }

    public bool OutputTabOrderDiffers(IReadOnlyList<string> open)
    {
        var order = OutputTabOrder(LabCatalog.OutputKindFor(ActiveSource));
        var expected = order.Where(open.Contains).ToList();
        var current = open.Where(order.Contains).ToList();
        return !expected.SequenceEqual(current, StringComparer.Ordinal);
    }

    public void AddOutputTab(string type)
    {
        var kind = LabCatalog.OutputKindFor(ActiveSource);
        var produced = LabCatalog.ProducedOutputTypes(ActiveSource);
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
        var kind = LabCatalog.OutputKindFor(ActiveSource);
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
        var kind = LabCatalog.OutputKindFor(ActiveSource);
        var tabs = OpenTabs(kind);
        var settingsOrder = OutputTabOrder(kind);
        foreach (var tab in OutputTabsFor(ActiveSource))
        {
            if (tabs.Contains(tab.Type))
            {
                continue;
            }

            var idRank = settingsOrder.IndexOf(tab.Type);
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

            tabs.Insert(insertAt, tab.Type);
        }

        Revision++;
        EnsureActiveOutput();
        Notify();
    }

    public void SaveOpenOutputTabsAsSettings()
    {
        var kind = LabCatalog.OutputKindFor(ActiveSource);
        var catalog = LabCatalog.CatalogFor(kind);
        var catalogIds = catalog.Select(tab => tab.Type).ToHashSet(StringComparer.Ordinal);
        var open = OpenTabs(kind)
            .Where(catalogIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!open.Contains(LabCatalog.ErrorsOutputType))
        {
            open.Add(LabCatalog.ErrorsOutputType);
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

        foreach (var tab in catalog)
        {
            if (seen.Add(tab.Type))
            {
                order.Add(tab.Type);
            }
        }

        _outputTabOrder[kind] = order;
        _hiddenOutputTabs[kind] = catalog
            .Select(tab => tab.Type)
            .Where(id => !open.Contains(id) && id != LabCatalog.ErrorsOutputType)
            .ToHashSet(StringComparer.Ordinal);
        Notify();
    }
    public void EnsureActiveOutput()
    {
        SyncOpenOutputKind();
        var tabs = CurrentOutputTabIds;
        if (tabs.Contains(ActiveOutput))
        {
            return;
        }

        SetActiveOutput(tabs.FirstOrDefault(id => id is "cs" or "gcs")
            ?? tabs.FirstOrDefault()
            ?? LabCatalog.ErrorsOutputType);
    }

    public IReadOnlyList<OutputTab> OutputTabsFor(string fileName)
    {
        var kind = LabCatalog.OutputKindFor(fileName);
        var catalog = LabCatalog.CatalogFor(kind);
        var byType = catalog.ToDictionary(tab => tab.Type, StringComparer.Ordinal);
        var produced = LabCatalog.ProducedOutputTypes(fileName);
        var tabs = new List<OutputTab>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in OutputTabOrder(kind))
        {
            if (HiddenOutputTabs(kind).Contains(id) || !produced.Contains(id) || !seen.Add(id))
            {
                continue;
            }

            if (byType.TryGetValue(id, out var tab))
            {
                tabs.Add(tab);
            }
        }

        foreach (var id in produced)
        {
            if (!byType.ContainsKey(id) && seen.Add(id))
            {
                tabs.Add(new OutputTab(id, LabCatalog.OutputTypeLabel(id)));
            }
        }

        if (produced.Contains(LabCatalog.FailOutputType) && seen.Add(LabCatalog.FailOutputType))
        {
            tabs.Add(new OutputTab(LabCatalog.FailOutputType, "Failure"));
        }

        return tabs;
    }

    public string OutputLabel(string type)
        => CurrentOutputTabs.FirstOrDefault(tab => tab.Type == type)?.Label ?? type;

    public string OutputTabTitle(string type)
        => type switch
        {
            "seq" => "Sequence points (seq)",
            _ => $"{OutputLabel(type)} ({type})"
        };
    private List<string> OpenTabs(OutputFileKind kind)
    {
        if (!_openOutputTabs.TryGetValue(kind, out var tabs))
        {
            var fileName = kind == LabCatalog.OutputKindFor(ActiveSource) ? ActiveSource : LabCatalog.RepresentativeFile(kind);
            tabs = OutputTabsFor(fileName).Select(tab => tab.Type).ToList();
            _openOutputTabs[kind] = tabs;
        }

        return tabs;
    }
    private void SyncOpenOutputKind()
    {
        var kind = LabCatalog.OutputKindFor(ActiveSource);
        var tabs = OpenTabs(kind);
        var produced = LabCatalog.ProducedOutputTypes(ActiveSource);
        tabs.RemoveAll(id => !produced.Contains(id));
        if (produced.Contains(LabCatalog.ErrorsOutputType) && !tabs.Contains(LabCatalog.ErrorsOutputType))
        {
            tabs.Add(LabCatalog.ErrorsOutputType);
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

    private List<string> OutputTabOrder(OutputFileKind kind)
        => _outputTabOrder[kind];

    private HashSet<string> HiddenOutputTabs(OutputFileKind kind)
        => _hiddenOutputTabs[kind];

    private static Dictionary<OutputFileKind, List<string>> CreateDefaultOutputTabOrder()
        => new()
        {
            [OutputFileKind.Cs] = LabCatalog.DefaultTabOrder(OutputFileKind.Cs),
            [OutputFileKind.Razor] = LabCatalog.DefaultTabOrder(OutputFileKind.Razor),
            [OutputFileKind.Cshtml] = LabCatalog.DefaultTabOrder(OutputFileKind.Cshtml)
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

        var catalog = LabCatalog.CatalogFor(kind).Select(tab => tab.Type).ToHashSet(StringComparer.Ordinal);
        var order = value.TryGetProperty("order", out var orderElement) && orderElement.ValueKind == JsonValueKind.Array
            ? SanitizeOrder(kind, ReadStringArray(orderElement))
            : LabCatalog.DefaultTabOrder(kind);
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (value.TryGetProperty("hidden", out var hiddenElement) && hiddenElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in ReadStringArray(hiddenElement))
            {
                if (catalog.Contains(id) && id != LabCatalog.ErrorsOutputType)
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
        var catalog = LabCatalog.CatalogFor(kind);
        var catalogIds = catalog.Select(tab => tab.Type).ToHashSet(StringComparer.Ordinal);
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

        foreach (var tab in catalog)
        {
            if (seen.Add(tab.Type))
            {
                order.Add(tab.Type);
                if (tab.Type != LabCatalog.ErrorsOutputType)
                {
                    hidden.Add(tab.Type);
                }
            }
        }

        if (!order.Contains(LabCatalog.ErrorsOutputType))
        {
            order.Add(LabCatalog.ErrorsOutputType);
        }

        hidden.Remove(LabCatalog.ErrorsOutputType);
        _outputTabOrder[kind] = order;
        _hiddenOutputTabs[kind] = hidden;
    }

    private static List<string> SanitizeOrder(OutputFileKind kind, IReadOnlyList<string> ids)
    {
        var catalog = LabCatalog.CatalogFor(kind);
        var catalogIds = catalog.Select(tab => tab.Type).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var id in ids)
        {
            if (catalogIds.Contains(id) && seen.Add(id))
            {
                order.Add(id);
            }
        }

        foreach (var tab in catalog)
        {
            if (seen.Add(tab.Type))
            {
                order.Add(tab.Type);
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
        _outputTabOrder[kind] = LabCatalog.DefaultTabOrder(kind);
        _hiddenOutputTabs[kind] = new HashSet<string>(StringComparer.Ordinal);
    }

    private void Notify()
    {
        Changed?.Invoke();
    }

    private void SetActiveOutput(string type) =>
        _dispatcher.Dispatch(new SetActiveOutputAction(type));
}
