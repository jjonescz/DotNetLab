namespace DotNetLab.Infrastructure.Worker;

/// <summary>
/// Host adapter for the existing <c>WorkerController.js</c> protocol:
/// JSON <c>WorkerInputMessage</c> / <c>WorkerOutputMessage</c> over
/// <c>postMessage</c>, plus the GC-dump side channel. Does not define a
/// new worker wire format.
/// </summary>
public interface IWorkerTransport
{
    bool SupportsBackgroundWorker { get; }

    Task EnsureControllerAsync();

    Task EnsureInProcessInteropAsync();

    IWorkerHandle CreateWorker(string scriptUrl, Action<string> onMessage, Action<string> onError);

    void WorkerReady(IWorkerHandle worker);

    void PostMessage(IWorkerHandle worker, string message);

    void PostSideMessage(IWorkerHandle worker, string message);

    void DisposeWorker(IWorkerHandle worker);

    void CollectAndDownloadGcDump();
}

public interface IWorkerHandle : IDisposable;

internal sealed class UnsupportedWorkerTransport : IWorkerTransport
{
    public bool SupportsBackgroundWorker => false;

    public Task EnsureControllerAsync() => Task.CompletedTask;

    public Task EnsureInProcessInteropAsync() => Task.CompletedTask;

    public IWorkerHandle CreateWorker(string scriptUrl, Action<string> onMessage, Action<string> onError)
        => throw new InvalidOperationException("Workers are only supported in the browser.");

    public void WorkerReady(IWorkerHandle worker)
    {
    }

    public void PostMessage(IWorkerHandle worker, string message)
    {
    }

    public void PostSideMessage(IWorkerHandle worker, string message)
    {
    }

    public void DisposeWorker(IWorkerHandle worker)
    {
    }

    public void CollectAndDownloadGcDump()
    {
    }
}
