namespace DotNetLab.Features.Workspace;

public sealed class EditorDragState
{
    public string? Kind { get; private set; }
    public string? Pane { get; private set; }
    public string? Group { get; private set; }
    public string? Value { get; private set; }

    public event Action? Changed;

    public void Begin(string kind, string pane, string group, string value)
    {
        Kind = kind;
        Pane = pane;
        Group = group;
        Value = value;
    }

    public void Clear()
    {
        Kind = null;
        Pane = null;
        Group = null;
        Value = null;
        Changed?.Invoke();
    }
}
