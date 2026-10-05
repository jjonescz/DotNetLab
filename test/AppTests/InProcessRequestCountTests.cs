using AwesomeAssertions;
using DotNetLab.Infrastructure.Worker;

namespace DotNetLab;

[TestClass]
public sealed class InProcessRequestCountTests
{
    [TestMethod]
    public async Task WhenIdle_CompletesImmediatelyIfEmpty()
    {
        var count = new InProcessRequestCount();
        count.Current.Should().Be(0);
        await count.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task WhenIdle_WaitsUntilEnterIsDisposed()
    {
        var count = new InProcessRequestCount();
        var lease = count.Enter();
        count.Current.Should().Be(1);

        var idle = count.WhenIdleAsync();
        idle.IsCompleted.Should().BeFalse();

        lease.Dispose();
        await idle.WaitAsync(TimeSpan.FromSeconds(1));
        count.Current.Should().Be(0);
    }

    [TestMethod]
    public async Task WhenIdle_WaitsForOverlappingEnters()
    {
        var count = new InProcessRequestCount();
        var first = count.Enter();
        var second = count.Enter();
        var idle = count.WhenIdleAsync();

        first.Dispose();
        await Task.Delay(20);
        idle.IsCompleted.Should().BeFalse();

        second.Dispose();
        await idle.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task WhenIdle_CanDrainAgainAfterIdle()
    {
        var count = new InProcessRequestCount();
        var first = count.Enter();
        var idle = count.WhenIdleAsync();
        first.Dispose();
        await idle.WaitAsync(TimeSpan.FromSeconds(1));

        var second = count.Enter();
        var again = count.WhenIdleAsync();
        again.IsCompleted.Should().BeFalse();
        second.Dispose();
        await again.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
