using AwesomeAssertions;
using DotNetLab.Features.Compiler;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetLab;

[TestClass]
public sealed class CompilationSchedulerTests
{
    [TestMethod]
    public async Task Enqueue_ReturnsBeforeExecuteCompletes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new CompilationScheduler(async (_, _) =>
        {
            started.SetResult();
            await release.Task;
        }, NullLogger.Instance);

        var enqueued = scheduler.EnqueueAsync(storeInCache: true, updateDisplayedOutput: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        enqueued.IsCompleted.Should().BeFalse();

        release.SetResult();
        await enqueued.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task MiddleRequest_IsDropped()
    {
        var executed = new List<int>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new CompilationScheduler(async (request, _) =>
        {
            executed.Add(request.Generation);
            if (request.Generation == 1)
            {
                firstStarted.SetResult();
                await firstRelease.Task;
            }
        }, NullLogger.Instance);

        var first = scheduler.EnqueueAsync(storeInCache: true, updateDisplayedOutput: true);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var middle = scheduler.EnqueueAsync(storeInCache: false, updateDisplayedOutput: false);
        var latest = scheduler.EnqueueAsync(storeInCache: true, updateDisplayedOutput: true);
        firstRelease.SetResult();

        await Task.WhenAll(first, middle, latest).WaitAsync(TimeSpan.FromSeconds(2));
        executed.Should().Equal(1, 3);
    }

    [TestMethod]
    public async Task DroppedRequest_KeepsEagerFlags()
    {
        CompileRequest? latest = null;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new CompilationScheduler(async (request, _) =>
        {
            if (request.Generation == 1)
            {
                firstStarted.SetResult();
                await firstRelease.Task;
                return;
            }

            latest = request;
        }, NullLogger.Instance);

        var first = scheduler.EnqueueAsync(storeInCache: true, updateDisplayedOutput: false);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var middle = scheduler.EnqueueAsync(storeInCache: true, updateDisplayedOutput: false);
        var third = scheduler.EnqueueAsync(storeInCache: false, updateDisplayedOutput: true);
        firstRelease.SetResult();

        await Task.WhenAll(first, middle, third).WaitAsync(TimeSpan.FromSeconds(2));
        latest.Should().NotBeNull();
        latest!.Value.StoreInCache.Should().BeTrue();
        latest.Value.UpdateDisplayedOutput.Should().BeTrue();
        latest.Value.Generation.Should().Be(3);
    }

    [TestMethod]
    public async Task DisposeAsync_WaitsUntilCancelledExecuteReturns()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executeEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new CompilationScheduler(async (_, ct) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                executeEnded.TrySetResult();
            }
        }, NullLogger.Instance);

        _ = scheduler.EnqueueAsync(storeInCache: true, updateDisplayedOutput: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        executeEnded.Task.IsCompleted.Should().BeTrue();
        scheduler.EnqueueAsync(storeInCache: true, updateDisplayedOutput: true).IsCanceled.Should().BeTrue();
    }
}
