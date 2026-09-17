namespace DotNetLab.Editor;

public sealed class EditorCursor
{
    public int Line { get; private set; } = 9;
    public int Column { get; private set; } = 34;

    public event Action? Changed;

    public void Set(int line, int column)
    {
        if (Line == line && Column == column)
        {
            return;
        }

        Line = line;
        Column = column;
        Changed?.Invoke();
    }
}
