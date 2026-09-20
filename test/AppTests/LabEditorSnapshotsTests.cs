using AwesomeAssertions;
using DotNetLab.Editor;

namespace DotNetLab;

[TestClass]
public sealed class LabEditorSnapshotsTests
{
    [TestMethod]
    public async Task FlushAsync_RunsRegisteredFlushers_AndSkipsUnregistered()
    {
        var snapshots = new LabEditorSnapshots();
        var ran = new List<string>();

        Task First()
        {
            ran.Add("first");
            return Task.CompletedTask;
        }

        Task Second()
        {
            ran.Add("second");
            return Task.CompletedTask;
        }

        snapshots.Register(First);
        snapshots.Register(Second);
        await snapshots.FlushAsync();
        ran.Should().Equal("first", "second");

        ran.Clear();
        snapshots.Unregister(First);
        await snapshots.FlushAsync();
        ran.Should().Equal("second");
    }
}
