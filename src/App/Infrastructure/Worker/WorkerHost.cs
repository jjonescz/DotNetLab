using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DotNetLab.Features.Preferences;
using DotNetLab.Infrastructure.Browser;
using DotNetLab.Infrastructure.Logging;
using Microsoft.JSInterop;
using Timer = System.Timers.Timer;

namespace DotNetLab.Infrastructure.Worker;

/// <summary>
/// Owns the compiler/worker, either in-process via <see cref="WorkerServices"/>
/// or in the existing <c>WorkerWebAssembly</c> web worker — same split as
/// <c>src/App</c> <c>WorkerController</c>. Background-worker vs in-process is
/// chosen on first use and requires a page reload to change.
/// Native hosts use in-process <see cref="WorkerServices"/> because
/// <see cref="IWorkerTransport.SupportsBackgroundWorker"/> is false.
/// Browser I/O goes through <see cref="IWorkerTransport"/> (existing
/// <c>WorkerController.js</c> protocol).
/// </summary>
public sealed class WorkerHost : IAsyncDisposable
{
    private readonly string _baseUrl;
    private readonly bool _supportsThreads;
    private readonly LabLogging _logging;
    private readonly LabSettings _settings;
    private readonly IWorkerTransport _transport;
    private readonly ILogger<WorkerHost> _logger;
    private readonly Dispatcher _dispatcher = Dispatcher.CreateDefault();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<WorkerOutputMessage>> _pending = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly InProcessRequestCount _inProcessRequests = new();
    private IServiceProvider? _services;
    private WorkerInstance? _worker;
    private bool? _useWorker;
    private int _messageId;
    private int _epoch;
    private bool _disposed;

    public WorkerHost(
        ILabEnvironment environment,
        LabLogging logging,
        LabSettings settings,
        IWorkerTransport transport,
        ILogger<WorkerHost> logger)
    {
        _baseUrl = environment.BaseAddress;
        _supportsThreads = environment.SupportsThreads;
        _logging = logging;
        _settings = settings;
        _transport = transport;
        _logger = logger;
    }

    public event Action<string>? Failed;

    /// <summary>
    /// Raised at the start of <see cref="RecreateAsync"/> / dispose, after the
    /// epoch advances. Language services cancel leftover deltas here so they
    /// are not applied to the next worker.
    /// </summary>
    internal event Action? Recreating;

    /// <summary>
    /// Raised after the replacement worker is running. Language services drain
    /// the cancelled queue and start a new reader.
    /// </summary>
    internal event Func<Task>? Recreated;

    public PingResult? LastPingResult { get; private set; }

    public int NextMessageId() => Interlocked.Increment(ref _messageId);

    public async ValueTask DisposeAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Interlocked.Increment(ref _epoch);
            Recreating?.Invoke();
            await DisposeCurrentNoLockAsync();
        }
        finally
        {
            _startLock.Release();
        }

        _startLock.Dispose();
    }

    public async Task RecreateAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Increment(ref _epoch);
            Recreating?.Invoke();
            await DisposeCurrentNoLockAsync();
            _useWorker ??= await LoadUseWorkerAsync();
            await StartNoLockAsync();
            await InvokeRecreatedAsync();
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task<T> SendAsync<T>(IWorkerInputMessage<T> message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenRegistration registration = default;
        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(() =>
            {
                _ = PostAsync(new WorkerInputMessage.Cancel(MessageIdToCancel: message.Id)
                {
                    Id = NextMessageId(),
                });
            });
        }

        try
        {
            var incoming = await PostAsync(message).WaitAsync(cancellationToken);
            return ReadResult<T>(incoming);
        }
        finally
        {
            await registration.DisposeAsync();
        }
    }

    public async Task CollectAndDownloadGcDumpAsync()
    {
        await EnsureStartedAsync();
        await _transport.EnsureControllerAsync();
        _transport.CollectAndDownloadGcDump();
        if (_useWorker == true && _worker is { } worker)
        {
            _transport.PostSideMessage(worker.Handle, "collect-gc-dump");
        }
    }

    private async Task<WorkerOutputMessage> PostAsync(IWorkerInputMessage message)
    {
        await EnsureStartedAsync();
        var epoch = Volatile.Read(ref _epoch);
        if (_disposed || epoch != Volatile.Read(ref _epoch))
        {
            return DisposedFailure(message);
        }

        if (_useWorker != true)
        {
            using var lease = _inProcessRequests.Enter();
            var services = Volatile.Read(ref _services);
            if (services is null || _disposed || epoch != Volatile.Read(ref _epoch))
            {
                return DisposedFailure(message);
            }

            var executor = services.GetRequiredService<WorkerInputMessage.IExecutor>();
            // Do not serialize in-process messages: Cancel must overlap the request it aborts,
            // matching src/App WorkerController (ungated HandleAndGetOutputAsync / Task.Run).
            // Recreate waits for _inProcessRequests to drain before disposing this provider.
            if (_supportsThreads)
            {
                _logger.Log(
                    message is WorkerInputMessage.Ping ? LogLevel.Trace : LogLevel.Debug,
                    "=> {Id}: {Type} (bg)",
                    message.Id,
                    message.GetType().Name);
                var background = await Task.Run(() => message.HandleAndGetOutputAsync(executor));
                return epoch == Volatile.Read(ref _epoch) ? background : DisposedFailure(message);
            }

            _logger.Log(
                message is WorkerInputMessage.Ping ? LogLevel.Trace : LogLevel.Debug,
                "=> {Id}: {Type} (fg)",
                message.Id,
                message.GetType().Name);
            var foreground = await message.HandleAndGetOutputAsync(executor);
            return epoch == Volatile.Read(ref _epoch) ? foreground : DisposedFailure(message);
        }

        var worker = _worker;
        if (worker is null)
        {
            return DisposedFailure(message);
        }

        var tcs = new TaskCompletionSource<WorkerOutputMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(message.Id, tcs))
        {
            throw new InvalidOperationException($"Request with ID {message.Id} already exists.");
        }

        var serialized = JsonSerializer.Serialize(message, WorkerJsonContext.Default.WorkerInputMessage);
        _logger.Log(
            message is WorkerInputMessage.Ping ? LogLevel.Trace : LogLevel.Debug,
            "=> {Id}: {Type} ({Details})",
            message.Id,
            message.GetType().Name,
            serialized.Length.SeparateThousands());

        try
        {
            if (epoch != Volatile.Read(ref _epoch) || !ReferenceEquals(worker, _worker))
            {
                _pending.TryRemove(message.Id, out _);
                return DisposedFailure(message);
            }

            _transport.PostMessage(worker.Handle, serialized);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sending worker message {Id} failed.", message.Id);
            _pending.TryRemove(message.Id, out _);
            return new WorkerOutputMessage.Failure(ex)
            {
                Id = message.Id,
                InputType = message.GetType().Name,
            };
        }

        return await tcs.Task;
    }

    private bool IsStarted => _worker is not null || _services is not null;

    private async Task EnsureStartedAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsStarted)
        {
            return;
        }

        await _startLock.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsStarted)
            {
                return;
            }

            _useWorker ??= await LoadUseWorkerAsync();
            await StartNoLockAsync();
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task<bool> LoadUseWorkerAsync()
    {
        if (!_transport.SupportsBackgroundWorker)
        {
            return false;
        }

        try
        {
            var snapshot = await _settings.LoadAsync();
            return snapshot?.BackgroundWorker ?? true;
        }
        catch (JSException)
        {
            return true;
        }
    }

    private async Task StartNoLockAsync()
    {
        if (_useWorker == true)
        {
            _logger.LogInformation("LANGUAGE SERVICES EXECUTION: browser worker");
            _worker = await CreateWorkerAsync();
            return;
        }

        _logger.LogInformation(
            _supportsThreads
                ? "LANGUAGE SERVICES EXECUTION: background .NET thread"
                : "LANGUAGE SERVICES EXECUTION: UI/foreground");
        await _transport.EnsureInProcessInteropAsync();
        _services = WorkerServices.Create(_baseUrl, _logging.LogLevel);
    }

    private async Task DisposeCurrentNoLockAsync()
    {
        if (_worker is { } worker)
        {
            try
            {
                worker.PingTimer.Stop();
                worker.PingTimer.Dispose();
                _transport.DisposeWorker(worker.Handle);
                worker.Handle.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Disposing worker failed.");
            }

            _worker = null;
        }

        if (_services is not null)
        {
            var services = _services;
            _services = null;
            await _inProcessRequests.WhenIdleAsync();
            await DisposeServicesAsync(services);
        }

        DiscardPending("Worker disposed");
    }

    private async Task<WorkerInstance> CreateWorkerAsync()
    {
        await _transport.EnsureControllerAsync();

        var epoch = Volatile.Read(ref _epoch);
        var pingTimer = new Timer(TimeSpan.FromSeconds(10));
        pingTimer.Elapsed += (_, _) =>
        {
            _ = _dispatcher.InvokeAsync(async () =>
            {
                pingTimer.Enabled = false;
                try
                {
                    LastPingResult = await SendAsync(new WorkerInputMessage.Ping { Id = NextMessageId() });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Worker ping failed.");
                }
                finally
                {
                    pingTimer.Enabled = true;
                }
            });
        };

        var workerReady = new TaskCompletionSource();
        Action<string> messageHandler = data =>
        {
            if (epoch != Volatile.Read(ref _epoch))
            {
                return;
            }

            _ = _dispatcher.InvokeAsync(() =>
            {
                if (epoch != Volatile.Read(ref _epoch))
                {
                    return Task.CompletedTask;
                }

                if (TryCompleteOversizedCompletion(data, out var dropped))
                {
                    _logger.LogWarning(
                        "Dropped oversized completion payload ({Size} bytes) for {Id}",
                        data.Length.SeparateThousands(),
                        dropped.Id);
                    if (_pending.TryRemove(dropped.Id, out var droppedTcs))
                    {
                        droppedTcs.TrySetResult(dropped);
                    }

                    return Task.CompletedTask;
                }

                var message = JsonSerializer.Deserialize(data, WorkerJsonContext.Default.WorkerOutputMessage)!;
                _logger.Log(
                    message.InputType == nameof(WorkerInputMessage.Ping) ? LogLevel.Trace : LogLevel.Debug,
                    "<= {Id}: {InputType} → {OutputType} ({Size})",
                    message.Id,
                    message.InputType,
                    message.GetType().Name,
                    data.Length.SeparateThousands());
                if (message is WorkerOutputMessage.Ready)
                {
                    workerReady.TrySetResult();
                }
                else if (message.Id < 0)
                {
                    _logger.LogError("Unpaired message {Message}", message);
                    DiscardPending("Unpaired message received", $"Unpaired message received: {message}");
                }
                else if (_pending.TryRemove(message.Id, out var tcs))
                {
                    tcs.TrySetResult(message);
                }
                else
                {
                    _logger.LogWarning("No pending request for message {Id}", message.Id);
                }

                return Task.CompletedTask;
            });
        };
        Action<string> errorHandler = error =>
        {
            if (epoch != Volatile.Read(ref _epoch))
            {
                return;
            }

            _logger.LogError("Worker error: {Error}", error);
            pingTimer.Stop();
            workerReady.TrySetException(new InvalidOperationException($"Worker error: {error}"));
            _ = _dispatcher.InvokeAsync(() =>
            {
                if (epoch != Volatile.Read(ref _epoch))
                {
                    return Task.CompletedTask;
                }

                DiscardPending("Worker error", error);
                Failed?.Invoke(error);
                return Task.CompletedTask;
            });
        };

        var handle = _transport.CreateWorker(
            GetWorkerUrl("../_content/DotNetLab.WorkerWebAssembly/main.js", [_baseUrl, _logging.LogLevel.ToString()]),
            messageHandler,
            errorHandler);
        await workerReady.Task;
        _transport.WorkerReady(handle);
        pingTimer.Start();
        return new WorkerInstance
        {
            Handle = handle,
            PingTimer = pingTimer,
            MessageHandler = messageHandler,
            ErrorHandler = errorHandler,
        };
    }

    internal static T ReadResult<T>(WorkerOutputMessage incoming)
        => incoming switch
        {
            WorkerOutputMessage.Success success => success.Result switch
            {
                null => default!,
                JsonElement json => json.Deserialize<T>(WorkerJsonContext.Default.Options)!,
                T result => result,
                var other => throw new InvalidOperationException(
                    $"Expected result of type '{typeof(T)}', got '{other.GetType()}': {other}"),
            },
            WorkerOutputMessage.Failure failure => throw new InvalidOperationException(failure.FullString),
            // NoOutput handlers (cached compilation, document/workspace mutations, cancel)
            // complete with Empty rather than Success(null).
            WorkerOutputMessage.Empty => default!,
            _ => throw new InvalidOperationException($"Unexpected message type: {incoming}"),
        };

    private async Task InvokeRecreatedAsync()
    {
        var handlers = Recreated;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            await ((Func<Task>)handler)();
        }
    }

    private static WorkerOutputMessage DisposedFailure(IWorkerInputMessage message)
        => new WorkerOutputMessage.Failure("Worker disposed")
        {
            Id = message.Id,
            InputType = message.GetType().Name,
        };

    private void DiscardPending(string message, string? fullString = null)
    {
        var failure = new WorkerOutputMessage.Failure(message, fullString ?? message)
        {
            Id = WorkerOutputMessage.BroadcastId,
            InputType = WorkerOutputMessage.BroadcastInputType,
        };
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
            {
                _logger.LogDebug("Discarding pending request {Id}", kvp.Key);
                tcs.TrySetResult(failure);
            }
        }
    }

    private static string GetWorkerUrl(string url, ReadOnlySpan<string> args)
    {
        var sb = new StringBuilder(url);
        var i = 0;
        foreach (var arg in args)
        {
            sb.Append(i++ == 0 ? '?' : '&');
            sb.Append("arg=");
            sb.Append(Uri.EscapeDataString(arg));
        }

        return sb.ToString();
    }

    private const int OversizedCompletionBytes = 64 * 1024;

    private static bool TryCompleteOversizedCompletion(string data, out WorkerOutputMessage.Success empty)
    {
        empty = null!;
        if (data.Length <= OversizedCompletionBytes)
        {
            return false;
        }

        var tailStart = Math.Max(0, data.Length - 256);
        if (!data.AsSpan(tailStart).Contains("ProvideCompletionItems", StringComparison.Ordinal) &&
            !data.AsSpan(0, Math.Min(256, data.Length)).Contains("ProvideCompletionItems", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryReadMessageId(data.AsSpan(tailStart), out var id) &&
            !TryReadMessageId(data.AsSpan(0, Math.Min(256, data.Length)), out id))
        {
            return false;
        }

        empty = new WorkerOutputMessage.Success("""{"suggestions":[]}""")
        {
            Id = id,
            InputType = nameof(WorkerInputMessage.ProvideCompletionItems),
        };
        return true;
    }

    private static bool TryReadMessageId(ReadOnlySpan<char> span, out int id)
    {
        id = 0;
        var at = span.LastIndexOf("\"Id\":");
        var markerLength = 5;
        if (at < 0)
        {
            at = span.LastIndexOf("\"id\":");
            markerLength = 5;
        }

        if (at < 0)
        {
            return false;
        }

        var digits = span[(at + markerLength)..].TrimStart();
        var end = 0;
        while (end < digits.Length && char.IsAsciiDigit(digits[end]))
        {
            end++;
        }

        return end > 0 && int.TryParse(digits[..end], out id);
    }

    private static async ValueTask DisposeServicesAsync(IServiceProvider services)
    {
        switch (services)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private sealed class WorkerInstance
    {
        public required IWorkerHandle Handle { get; init; }
        public required Timer PingTimer { get; init; }
        public required Action<string> MessageHandler { get; init; }
        public required Action<string> ErrorHandler { get; init; }
    }
}
