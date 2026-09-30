using DotNetLab.Features.Documents;

namespace DotNetLab.Features.Outputs;

public sealed partial class OutputWorkspace
{
    public IReadOnlyList<string> OpenIds
    {
        get
        {
            var kind = OutputCatalog.KindFor(ActiveDocument);
            var produced = OutputCatalog.ProducedTypes(kind);
            return OpenTabs(kind)
                .Where(produced.Contains)
                .ToArray();
        }
    }

    public IReadOnlyList<OutputDefinition> SettingsRowsFor(DocumentKind kind)
    {
        var catalog = OutputCatalog.For(kind);
        var byId = catalog.ToDictionary(output => output.Id, StringComparer.Ordinal);
        var rows = new List<OutputDefinition>(catalog.Count);
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

    public bool IsOutputTabVisible(DocumentKind kind, string type)
        => !HiddenOutputTabs(kind).Contains(type);

    public bool CanMoveOutputTab(DocumentKind kind, string type, int delta)
    {
        var order = OutputTabOrder(kind);
        var index = order.IndexOf(type);
        var next = index + delta;
        return index >= 0 && next >= 0 && next < order.Count;
    }

    public void SetOutputTabVisible(DocumentKind kind, string type, bool visible)
    {
        if (OutputCatalog.IsLocked(type) && !visible)
        {
            return;
        }

        if (!OutputCatalog.For(kind).Any(output => output.Id == type))
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

    public void MoveOutputTab(DocumentKind kind, string type, int delta)
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

    public void ResetOutputTabs(DocumentKind kind)
    {
        _outputTabOrder[kind] = OutputCatalog.DefaultOrder(kind);
        _hiddenOutputTabs[kind] = new HashSet<string>(StringComparer.Ordinal);
        _openOutputTabs.Remove(kind);
        if (kind == OutputCatalog.KindFor(ActiveDocument))
        {
            Revision++;
        }

        EnsureActiveOutput();
        Notify();
    }

    public void CaptureOpenOutputTabs(IReadOnlyList<string> ids)
    {
        var kind = OutputCatalog.KindFor(ActiveDocument);
        var produced = OutputCatalog.ProducedTypes(kind);
        var catalog = OutputCatalog.For(kind).Select(output => output.Id).ToHashSet(StringComparer.Ordinal);
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

    public IReadOnlyList<OutputDefinition> AddableOutputTabsFor(IReadOnlyList<string> open)
    {
        var openSet = open.ToHashSet(StringComparer.Ordinal);
        var kind = OutputCatalog.KindFor(ActiveDocument);
        var produced = OutputCatalog.ProducedTypes(kind);
        return OutputCatalog.For(kind)
            .Where(output => produced.Contains(output.Id) && !openSet.Contains(output.Id))
            .ToArray();
    }

    public bool HasClosedOutputTabs(IReadOnlyList<string> open)
    {
        var openSet = open.ToHashSet(StringComparer.Ordinal);
        return VisibleTabsFor(OutputCatalog.KindFor(ActiveDocument)).Any(output => !openSet.Contains(output.Id));
    }

    public bool OutputTabOrderDiffers(IReadOnlyList<string> open)
    {
        var order = OutputTabOrder(OutputCatalog.KindFor(ActiveDocument));
        var expected = order.Where(open.Contains).ToList();
        var current = open.Where(order.Contains).ToList();
        return !expected.SequenceEqual(current, StringComparer.Ordinal);
    }

    public void AddOutputTab(string type)
    {
        var kind = OutputCatalog.KindFor(ActiveDocument);
        var produced = OutputCatalog.ProducedTypes(kind);
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
        var kind = OutputCatalog.KindFor(ActiveDocument);
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
        var kind = OutputCatalog.KindFor(ActiveDocument);
        var tabs = OpenTabs(kind);
        var settingsOrder = OutputTabOrder(kind);
        foreach (var output in VisibleTabsFor(kind))
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
        var kind = OutputCatalog.KindFor(ActiveDocument);
        var catalog = OutputCatalog.For(kind);
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

        SetActiveOutput(tabs.FirstOrDefault(id => id == OutputCatalog.Cs.Id || id == OutputCatalog.Gcs.Id)
            ?? tabs.FirstOrDefault()
            ?? OutputCatalog.ErrorsId);
    }

    private IReadOnlyList<OutputDefinition> VisibleTabsFor(DocumentKind kind)
    {
        var catalog = OutputCatalog.For(kind);
        var byId = catalog.ToDictionary(output => output.Id, StringComparer.Ordinal);
        var produced = OutputCatalog.ProducedTypes(kind);
        var tabs = new List<OutputDefinition>();
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

    private List<string> OpenTabs(DocumentKind kind)
    {
        if (!_openOutputTabs.TryGetValue(kind, out var tabs))
        {
            tabs = VisibleTabsFor(kind).Select(output => output.Id).ToList();
            _openOutputTabs[kind] = tabs;
        }

        return tabs;
    }

    private void SyncOpenOutputKind()
    {
        var kind = OutputCatalog.KindFor(ActiveDocument);
        var tabs = OpenTabs(kind);
        var produced = OutputCatalog.ProducedTypes(kind);
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

    private List<string> OutputTabOrder(DocumentKind kind) => _outputTabOrder[kind];

    private HashSet<string> HiddenOutputTabs(DocumentKind kind) => _hiddenOutputTabs[kind];

    private static Dictionary<DocumentKind, List<string>> CreateDefaultOutputTabOrder()
        => new()
        {
            [DocumentKind.Cs] = OutputCatalog.DefaultOrder(DocumentKind.Cs),
            [DocumentKind.Razor] = OutputCatalog.DefaultOrder(DocumentKind.Razor),
            [DocumentKind.Cshtml] = OutputCatalog.DefaultOrder(DocumentKind.Cshtml)
        };

    private static Dictionary<DocumentKind, HashSet<string>> CreateDefaultHiddenOutputTabs()
        => new()
        {
            [DocumentKind.Cs] = new(StringComparer.Ordinal),
            [DocumentKind.Razor] = new(StringComparer.Ordinal),
            [DocumentKind.Cshtml] = new(StringComparer.Ordinal)
        };

    private void SetActiveOutput(string type)
        => _dispatcher.Dispatch(new SetActiveOutputAction(type));
}
