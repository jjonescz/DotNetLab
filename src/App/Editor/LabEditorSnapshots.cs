namespace DotNetLab.Editor;

public sealed class LabEditorSnapshots
{
    private readonly List<Func<Task>> _flushers = [];

    public void Register(Func<Task> flush) => _flushers.Add(flush);

    public void Unregister(Func<Task> flush) => _flushers.Remove(flush);

    public async Task FlushAsync()
    {
        foreach (var flush in _flushers.ToArray())
        {
            await flush();
        }
    }
}
