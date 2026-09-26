using AwesomeAssertions;
using DotNetLab.Features.Preferences;
using DotNetLab.Infrastructure.Browser;
using DotNetLab.Infrastructure.Logging;
using DotNetLab.Infrastructure.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using System.Text.Json;

namespace DotNetLab;

[TestClass]
public sealed class WorkerHostSendTests
{
    [TestMethod]
    public async Task SendAsync_AlreadyCancelled_ThrowsWithoutStarting()
    {
        await using var host = new WorkerHost(
            new LabEnvironment(IsDevelopment: false, BaseAddress: "http://localhost/"),
            new LabLogging(),
            new SettingsStore(new UnusedJsRuntime(), NullLogger<SettingsStore>.Instance),
            new UnsupportedWorkerTransport(),
            NullLogger<WorkerHost>.Instance);

        var act = async () => await host.SendAsync(
            new WorkerInputMessage.Ping { Id = host.NextMessageId() },
            new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
        host.LastPingResult.Should().BeNull();
    }

    [TestMethod]
    public async Task SendAsync_CancelledWhileWaiting_UnblocksWithoutResult()
    {
        var transport = new HangingWorkerTransport();
        await using var host = new WorkerHost(
            new LabEnvironment(IsDevelopment: false, BaseAddress: "http://localhost/"),
            new LabLogging(),
            new SettingsStore(new EmptyPrefsJsRuntime(), NullLogger<SettingsStore>.Instance),
            transport,
            NullLogger<WorkerHost>.Instance);

        using var cts = new CancellationTokenSource();
        var send = host.SendAsync(new WorkerInputMessage.Ping { Id = host.NextMessageId() }, cts.Token);
        await transport.Posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        var act = async () => await send;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task SendAsync_UnsupportedTransport_UsesInProcessInsteadOfCreateWorker()
    {
        var transport = new SpyInProcessTransport();
        var logger = new ListLogger();
        await using var host = new WorkerHost(
            new LabEnvironment(IsDevelopment: false, BaseAddress: "http://localhost/"),
            new LabLogging(),
            new SettingsStore(new EmptyPrefsJsRuntime(), NullLogger<SettingsStore>.Instance),
            transport,
            logger);

        var ping = await host.SendAsync(new WorkerInputMessage.Ping { Id = host.NextMessageId() });

        ping.Should().NotBeNull();
        transport.CreateWorkerCalls.Should().Be(0);
        transport.InProcessCalls.Should().Be(1);
        host.LastPingResult.Should().BeNull();
        logger.Messages.Should().Contain("Compiler running on the UI thread.");
    }

    [TestMethod]
    public async Task SendAsync_SupportsThreads_StillUsesInProcess()
    {
        var transport = new SpyInProcessTransport();
        var logger = new ListLogger();
        await using var host = new WorkerHost(
            new LabEnvironment(IsDevelopment: false, BaseAddress: "http://localhost/", SupportsThreads: true),
            new LabLogging(),
            new SettingsStore(new EmptyPrefsJsRuntime(), NullLogger<SettingsStore>.Instance),
            transport,
            logger);

        var ping = await host.SendAsync(new WorkerInputMessage.Ping { Id = host.NextMessageId() });

        ping.Should().NotBeNull();
        transport.CreateWorkerCalls.Should().Be(0);
        transport.InProcessCalls.Should().Be(1);
        logger.Messages.Should().Contain("Compiler running on a background .NET thread.");
    }

    [TestMethod]
    public async Task SendAsync_InProcess_InvokesWorkerConfigurer()
    {
        var configurer = new RecordingWorkerConfigurer();
        await using var host = new WorkerHost(
            new LabEnvironment(IsDevelopment: false, BaseAddress: "http://localhost/"),
            new LabLogging(),
            new SettingsStore(new EmptyPrefsJsRuntime(), NullLogger<SettingsStore>.Instance),
            new SpyInProcessTransport(),
            NullLogger<WorkerHost>.Instance,
            configurer);

        await host.SendAsync(new WorkerInputMessage.Ping { Id = host.NextMessageId() });

        configurer.Calls.Should().Be(1);
    }

    private sealed class RecordingWorkerConfigurer : IWorkerConfigurer
    {
        public int Calls { get; private set; }

        public void ConfigureWorkerServices(ServiceCollection services)
        {
            Calls++;
            _ = services;
        }
    }

    private sealed class SpyInProcessTransport : IWorkerTransport
    {
        public int CreateWorkerCalls { get; private set; }

        public int InProcessCalls { get; private set; }

        public bool SupportsBackgroundWorker => false;

        public Task EnsureControllerAsync() => Task.CompletedTask;

        public Task EnsureInProcessInteropAsync()
        {
            InProcessCalls++;
            return Task.CompletedTask;
        }

        public IWorkerHandle CreateWorker(string scriptUrl, Action<string> onMessage, Action<string> onError)
        {
            CreateWorkerCalls++;
            throw new InvalidOperationException("Workers are only supported in the browser.");
        }

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

    private sealed class HangingWorkerTransport : IWorkerTransport
    {
        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SupportsBackgroundWorker => true;

        public Task EnsureControllerAsync() => Task.CompletedTask;

        public Task EnsureInProcessInteropAsync() => Task.CompletedTask;

        public IWorkerHandle CreateWorker(string scriptUrl, Action<string> onMessage, Action<string> onError)
        {
            _ = scriptUrl;
            _ = onError;
            onMessage(JsonSerializer.Serialize(new WorkerOutputMessage.Ready
            {
                Id = WorkerOutputMessage.BroadcastId,
                InputType = WorkerOutputMessage.NoInputType,
            }, WorkerJsonContext.Default.WorkerOutputMessage));
            return new Handle();
        }

        public void WorkerReady(IWorkerHandle worker)
        {
        }

        public void PostMessage(IWorkerHandle worker, string message)
        {
            _ = worker;
            _ = message;
            Posted.TrySetResult();
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

        private sealed class Handle : IWorkerHandle
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class EmptyPrefsJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (typeof(TValue) == typeof(string))
            {
                return (ValueTask<TValue>)(object)new ValueTask<string>("");
            }

            return default;
        }
    }

    private sealed class UnusedJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => throw new NotSupportedException();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => throw new NotSupportedException();
    }

    private sealed class ListLogger : ILogger<WorkerHost>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
