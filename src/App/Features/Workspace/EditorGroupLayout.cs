namespace DotNetLab.Features.Workspace;

public sealed class EditorGroup
{
    public required string Id { get; init; }
    public required List<string> Items { get; init; }
    public required string Active { get; set; }
}

public sealed class EditorGroupLayout(string paneId)
{
    private List<EditorGroup> _groups = [];
    private int _nextGroupId = 1;

    public IReadOnlyList<EditorGroup> Groups => _groups;

    public string Orientation { get; private set; } = "vertical";

    public IReadOnlyList<string> PinnedOrder { get; set; } = [];

    public IReadOnlyList<string> LockedItems { get; set; } = [];

    public bool AllowClose { get; set; }

    public bool IsInitialized { get; private set; }

    public IReadOnlyList<string> CurrentTabIds()
        => _groups.SelectMany(group => group.Items).ToArray();

    public bool CanCloseItem(string item)
        => AllowClose
           && _groups.Sum(group => group.Items.Count) > 1
           && !IsLockedItem(item);

    public bool IsLockedItem(string item)
        => LockedItems.Count > 0 && LockedItems.Contains(item);

    public bool IsPinnedItem(string item)
        => PinnedOrder.Count > 0 && PinnedOrder.Contains(item);

    public bool IsFirst(EditorGroup group)
        => _groups.Count > 0 && ReferenceEquals(group, _groups[0]);

    public IEnumerable<(string Item, int Index)> UserTabs(EditorGroup group)
        => group.Items.Select((item, index) => (item, index)).Where(entry => !IsPinnedItem(entry.item));

    public IEnumerable<string> PinnedTabs(EditorGroup group)
        => PinnedOrder.Count > 0
            ? PinnedOrder.Where(group.Items.Contains)
            : [];

    public string TabClass(EditorGroup group, string item, string paneActive, bool pin = false, bool editing = false)
    {
        var selected = string.Equals(group.Active, item, StringComparison.Ordinal);
        var focused = string.Equals(paneActive, item, StringComparison.Ordinal);
        return "lab-tab"
            + (pin ? " lab-tab-pin" : null)
            + (selected ? " selected" : null)
            + (focused ? " focused" : null)
            + (editing ? " lab-tab-editing" : null);
    }

    public int LastUserInsertIndex(EditorGroup group)
    {
        var firstPinned = group.Items.FindIndex(IsPinnedItem);
        return firstPinned < 0 ? group.Items.Count : firstPinned;
    }

    public bool TryInitialize(IReadOnlyList<string> items, string active)
    {
        if (IsInitialized || items.Count == 0)
        {
            return false;
        }

        _groups =
        [
            new EditorGroup
            {
                Id = NextGroupId(),
                Items = items.ToList(),
                Active = string.IsNullOrWhiteSpace(active) ? items[0] : active
            }
        ];
        IsInitialized = true;
        return true;
    }

    public void SyncFromItems(IReadOnlyList<string> items, string active)
    {
        var known = items.ToHashSet(StringComparer.Ordinal);
        foreach (var group in _groups)
        {
            group.Items.RemoveAll(item => !known.Contains(item));
            if (group.Items.Count > 0 && !group.Items.Contains(group.Active))
            {
                group.Active = group.Items[0];
            }
        }

        _groups = _groups.Where(group => group.Items.Count > 0).ToList();

        foreach (var item in items)
        {
            if (_groups.All(group => !group.Items.Contains(item)))
            {
                if (_groups.Count == 0)
                {
                    _groups.Add(new EditorGroup
                    {
                        Id = $"{paneId}-1",
                        Items = [item],
                        Active = item
                    });
                }
                else
                {
                    _groups[0].Items.Add(item);
                    _groups[0].Active = item;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(active))
        {
            return;
        }

        var owner = _groups.FirstOrDefault(group => group.Items.Contains(active));
        if (owner is not null)
        {
            owner.Active = active;
        }
    }

    public void ReplaceFromItems(IReadOnlyList<string> items, string active)
    {
        if (items.Count == 0)
        {
            _groups = [];
            return;
        }

        var nextActive = !string.IsNullOrWhiteSpace(active) && items.Contains(active)
            ? active
            : items[0];
        _groups =
        [
            new EditorGroup
            {
                Id = _groups.FirstOrDefault()?.Id ?? $"{paneId}-1",
                Items = items.ToList(),
                Active = nextActive
            }
        ];
    }

    public void Select(string groupId, string value)
    {
        var group = _groups.First(item => item.Id == groupId);
        group.Active = value;
    }

    public EditorGroupCloseResult Close(string groupId, string value)
    {
        if (!CanCloseItem(value))
        {
            return EditorGroupCloseResult.Ignored;
        }

        var group = _groups.FirstOrDefault(item => item.Id == groupId);
        if (group is null || !group.Items.Contains(value))
        {
            return EditorGroupCloseResult.Ignored;
        }

        var wasActive = group.Active == value;
        group.Items.Remove(value);
        if (wasActive)
        {
            group.Active = group.Items.FirstOrDefault() ?? value;
        }

        _groups = _groups.Where(item => item.Items.Count > 0).ToList();
        var next = wasActive && _groups.Count > 0
            ? (_groups.FirstOrDefault(item => item.Id == groupId) ?? _groups[0]).Active
            : null;
        return new EditorGroupCloseResult(true, wasActive, next);
    }

    public bool TryApplyRename(string from, string to, IReadOnlyList<string> items)
    {
        if (!CanApplyRename(from, to, items))
        {
            return false;
        }

        var group = _groups.FirstOrDefault(item => item.Items.Contains(from));
        if (group is null)
        {
            return false;
        }

        var index = group.Items.IndexOf(from);
        group.Items[index] = to;
        if (group.Active == from)
        {
            group.Active = to;
        }

        return true;
    }

    public bool CanApplyRename(string from, string to, IReadOnlyList<string> items)
    {
        if (string.IsNullOrWhiteSpace(to) ||
            string.Equals(from, to, StringComparison.Ordinal) ||
            to is "." or ".." ||
            to.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetFileName(to), to, StringComparison.Ordinal) ||
            IsPinnedItem(from) ||
            IsPinnedItem(to) ||
            items.Contains(to))
        {
            return false;
        }

        return true;
    }

    public void MoveTab(string fromId, string value, string toId, int index)
    {
        if (IsPinnedItem(value))
        {
            return;
        }

        var source = _groups.FirstOrDefault(group => group.Id == fromId);
        var target = _groups.FirstOrDefault(group => group.Id == toId);
        if (source is null || target is null)
        {
            return;
        }

        source.Items.Remove(value);
        if (source.Active == value)
        {
            source.Active = source.Items.FirstOrDefault() ?? value;
        }

        var at = Math.Clamp(index, 0, LastUserInsertIndex(target));
        if (!target.Items.Contains(value))
        {
            target.Items.Insert(at, value);
        }

        target.Active = value;
        _groups = _groups.Where(group => group.Items.Count > 0).ToList();
    }

    public void SplitTab(DropZone zone, string value, string fromId, string toId)
    {
        if (IsPinnedItem(value))
        {
            return;
        }

        var source = _groups.FirstOrDefault(group => group.Id == fromId);
        var target = _groups.FirstOrDefault(group => group.Id == toId);
        if (source is null || target is null)
        {
            return;
        }

        if (source.Id == target.Id && source.Items.Count < 2)
        {
            return;
        }

        source.Items.Remove(value);
        if (source.Active == value)
        {
            source.Active = source.Items.FirstOrDefault() ?? value;
        }

        Orientation = zone is DropZone.Top or DropZone.Bottom ? "vertical" : "horizontal";

        EditorGroup created;
        if (source.Items.Count == 0)
        {
            source.Items.Add(value);
            source.Active = value;
            created = source;
        }
        else
        {
            created = new EditorGroup
            {
                Id = NextGroupId(),
                Items = [value],
                Active = value
            };
        }

        var remaining = _groups.Where(group => group.Items.Count > 0).ToList();
        remaining.Remove(created);

        var index = remaining.FindIndex(group => group.Id == target.Id);
        if (index < 0)
        {
            index = remaining.Count;
        }

        if (zone is DropZone.Top or DropZone.Left)
        {
            remaining.Insert(index, created);
        }
        else
        {
            remaining.Insert(Math.Min(index + 1, remaining.Count), created);
        }

        _groups = remaining;
    }

    private string NextGroupId() => $"{paneId}-{_nextGroupId++}";
}

public readonly record struct EditorGroupCloseResult(bool Closed, bool WasActive, string? NextActive)
{
    public static EditorGroupCloseResult Ignored { get; } = new(false, false, null);
}
