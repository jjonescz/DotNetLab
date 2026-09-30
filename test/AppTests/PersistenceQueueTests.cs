using AwesomeAssertions;
using DotNetLab.Features.Sharing;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetLab;

[TestClass]
public sealed class PersistenceQueueTests
{
    [TestMethod]
    public async Task Enqueue_ReturnsBeforeExecuteCompletes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new PersistenceQueue(async _ =>
        {
            started.SetResult();
            await release.Task;
        }, NullLogger.Instance, TimeSpan.Zero);

        var enqueued = queue.EnqueueAsync(PersistKind.Url);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        enqueued.IsCompleted.Should().BeFalse();

        release.SetResult();
        await enqueued.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task Burst_CoalescesKindsIntoOneExecute()
    {
        var executed = new List<PersistKind>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new PersistenceQueue(async kind =>
        {
            executed.Add(kind);
            if (executed.Count == 1)
            {
                firstStarted.SetResult();
                await firstRelease.Task;
            }
        }, NullLogger.Instance, TimeSpan.Zero);

        var first = queue.EnqueueAsync(PersistKind.Url);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var settings = queue.EnqueueAsync(PersistKind.Settings);
        var tabs = queue.EnqueueAsync(PersistKind.OutputTabs);
        firstRelease.SetResult();

        await Task.WhenAll(first, settings, tabs).WaitAsync(TimeSpan.FromSeconds(2));
        executed.Should().HaveCount(2);
        executed[0].Should().Be(PersistKind.Url);
        executed[1].Should().Be(PersistKind.Settings | PersistKind.OutputTabs);
    }

    [TestMethod]
    public async Task Debounce_MergesFlagsQueuedDuringDelay()
    {
        var executed = new List<PersistKind>();
        await using var queue = new PersistenceQueue(kind =>
        {
            executed.Add(kind);
            return Task.CompletedTask;
        }, NullLogger.Instance, TimeSpan.FromMilliseconds(40));

        var url = queue.EnqueueAsync(PersistKind.Url);
        var settings = queue.EnqueueAsync(PersistKind.Settings);
        await Task.WhenAll(url, settings).WaitAsync(TimeSpan.FromSeconds(2));

        executed.Should().Equal(PersistKind.Url | PersistKind.Settings);
    }

    [TestMethod]
    public async Task Debounce_WaiterQueuedDuringDelay_CompletesWithMergedFlags()
    {
        var executed = new List<PersistKind>();
        var firstPulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new PersistenceQueue(kind =>
        {
            executed.Add(kind);
            firstPulse.TrySetResult();
            return Task.CompletedTask;
        }, NullLogger.Instance, TimeSpan.FromMilliseconds(80));

        var url = queue.EnqueueAsync(PersistKind.Url);
        await Task.Delay(25);
        var settings = queue.EnqueueAsync(PersistKind.Settings);
        settings.IsCompleted.Should().BeFalse();

        await Task.WhenAll(url, settings, firstPulse.Task).WaitAsync(TimeSpan.FromSeconds(2));
        executed.Should().Equal(PersistKind.Url | PersistKind.Settings);
    }

    [TestMethod]
    public async Task DisposeAsync_WaitsForInFlightExecute()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executing = false;
        var queue = new PersistenceQueue(async _ =>
        {
            executing = true;
            started.SetResult();
            await release.Task;
            executing = false;
        }, NullLogger.Instance, TimeSpan.Zero);

        _ = queue.EnqueueAsync(PersistKind.Url);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var dispose = queue.DisposeAsync().AsTask();
        dispose.IsCompleted.Should().BeFalse();
        executing.Should().BeTrue();

        release.SetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        executing.Should().BeFalse();
        queue.EnqueueAsync(PersistKind.Url).IsCanceled.Should().BeTrue();
    }
}
