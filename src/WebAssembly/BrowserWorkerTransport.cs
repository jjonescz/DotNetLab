using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using DotNetLab.Infrastructure.Worker;

namespace DotNetLab;

[SupportedOSPlatform("browser")]
internal sealed class BrowserWorkerTransport : IWorkerTransport
{
    private readonly Lazy<Task> _controllerJs = new(() =>
        JSHost.ImportAsync("WorkerHost", "../js/WorkerHost.js"));

    public bool SupportsBackgroundWorker => true;

    public Task EnsureControllerAsync() => _controllerJs.Value;

    public Task EnsureInProcessInteropAsync() =>
        JSHost.ImportAsync("worker-interop.js", "../_content/DotNetLab.WorkerWebAssembly/interop.js");

    [SuppressMessage("Reliability", "CA2000:Call Dispose on object created", Justification = "JsWorkerHandle takes ownership of the JSObject.")]
    public IWorkerHandle CreateWorker(string scriptUrl, Action<string> onMessage, Action<string> onError)
        => new JsWorkerHandle(WorkerHostInterop.CreateWorker(scriptUrl, onMessage, onError));

    public void WorkerReady(IWorkerHandle worker)
        => WorkerHostInterop.WorkerReady(Js(worker));

    public void PostMessage(IWorkerHandle worker, string message)
        => WorkerHostInterop.PostMessage(Js(worker), message);

    public void PostSideMessage(IWorkerHandle worker, string message)
        => WorkerHostInterop.PostSideMessage(Js(worker), message);

    public void DisposeWorker(IWorkerHandle worker)
        => WorkerHostInterop.DisposeWorker(Js(worker));

    public void CollectAndDownloadGcDump()
        => WorkerHostInterop.CollectAndDownloadGcDump();

    private static JSObject Js(IWorkerHandle worker) =>
        worker is JsWorkerHandle handle
            ? handle.Js
            : throw new ArgumentException("Worker handle is not a JS worker.", nameof(worker));
}

[SupportedOSPlatform("browser")]
file sealed class JsWorkerHandle(JSObject js) : IWorkerHandle
{
    public JSObject Js { get; } = js;

    public void Dispose() => Js.Dispose();
}

[SupportedOSPlatform("browser")]
internal static partial class WorkerHostInterop
{
    [JSImport("createWorker", "WorkerHost")]
    public static partial JSObject CreateWorker(
        string scriptUrl,
        [JSMarshalAs<JSType.Function<JSType.String>>]
        Action<string> messageHandler,
        [JSMarshalAs<JSType.Function<JSType.String>>]
        Action<string> errorHandler);

    [JSImport("workerReady", "WorkerHost")]
    public static partial void WorkerReady(JSObject workerSetup);

    [JSImport("postMessage", "WorkerHost")]
    public static partial void PostMessage(JSObject workerSetup, string message);

    [JSImport("postSideMessage", "WorkerHost")]
    public static partial void PostSideMessage(JSObject workerSetup, string message);

    [JSImport("disposeWorker", "WorkerHost")]
    public static partial void DisposeWorker(JSObject workerSetup);

    [JSImport("collectAndDownloadGcDump", "WorkerHost")]
    public static partial void CollectAndDownloadGcDump();
}
