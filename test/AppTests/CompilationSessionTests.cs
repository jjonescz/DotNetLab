using System.Text.Json;
using AwesomeAssertions;
using DotNetLab.Editor;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Features.Outputs;
using DotNetLab.Features.Preferences;
using DotNetLab.Infrastructure.Browser;
using DotNetLab.Infrastructure.Logging;
using DotNetLab.Infrastructure.Caching.Compilation;
using DotNetLab.Infrastructure.Caching.Template;
using DotNetLab.Infrastructure.Worker;
using DotNetLab.Lab;
using Fluxor;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace DotNetLab;

[TestClass]
public sealed class CompilationSessionTests
{
    [TestMethod]
    public async Task QuietCompile_DoesNotOverlapAnotherCompile()
    {
        using var context = ImmediateSynchronizationContext.Install();
        var transport = new DelayedCompileTransport();
        await using var worker = CreateWorker(transport);
        var compilation = new Store<CompilationState>(new CompilationState());
        var dispatcher = new RecordingDispatcher(compilation);
        var created = CreateSession(worker, compilation, dispatcher);
        await using var session = created.Session;

        var first = session.CompileAsync(storeInCache: false, updateDisplayedOutput: false);
        await transport.WaitStartedAsync(0).WaitAsync(TimeSpan.FromSeconds(2));

        var second = session.CompileAsync(storeInCache: false, updateDisplayedOutput: false);
        transport.Release(0);
        await transport.WaitStartedAsync(1).WaitAsync(TimeSpan.FromSeconds(2));
        transport.CompileCount.Should().Be(2);
        transport.Release(1);

        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        dispatcher.Actions.OfType<SetRunningAction>().Should().BeEmpty();
    }

    [TestMethod]
    public async Task BusyCompile_SetsRunningWhileInFlight()
    {
        var transport = new DelayedCompileTransport();
        await using var worker = CreateWorker(transport);
        var compilation = new Store<CompilationState>(new CompilationState());
        var dispatcher = new RecordingDispatcher(compilation);
        var created = CreateSession(worker, compilation, dispatcher);
        await using var session = created.Session;

        var compile = session.CompileAsync(storeInCache: true, updateDisplayedOutput: true);
        await transport.WaitStartedAsync(0).WaitAsync(TimeSpan.FromSeconds(2));
        compilation.Value.Running.Should().BeTrue();

        transport.Release(0);
        await compile.WaitAsync(TimeSpan.FromSeconds(2));
        compilation.Value.Running.Should().BeFalse();
    }

    [TestMethod]
    public async Task Compile_SameInput_StillSendsAndShowsBusy()
    {
        var transport = new DelayedCompileTransport();
        await using var worker = CreateWorker(transport);
        var compilation = new Store<CompilationState>(new CompilationState());
        var dispatcher = new RecordingDispatcher(compilation);
        var created = CreateSession(worker, compilation, dispatcher);
        await using var session = created.Session;

        var first = session.CompileAsync(storeInCache: true, updateDisplayedOutput: true);
        await transport.WaitStartedAsync(0).WaitAsync(TimeSpan.FromSeconds(2));
        transport.Release(0);
        await first.WaitAsync(TimeSpan.FromSeconds(2));

        var second = session.CompileAsync(storeInCache: true, updateDisplayedOutput: true);
        await transport.WaitStartedAsync(1).WaitAsync(TimeSpan.FromSeconds(2));
        compilation.Value.Running.Should().BeTrue();
        transport.CompileCount.Should().Be(2);

        transport.Release(1);
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        compilation.Value.Running.Should().BeFalse();
    }

    [TestMethod]
    public async Task LatestCompile_IsPreferredOverQueuedMiddle()
    {
        var transport = new DelayedCompileTransport();
        await using var worker = CreateWorker(transport);
        var compilation = new Store<CompilationState>(new CompilationState());
        var dispatcher = new RecordingDispatcher(compilation);
        var created = CreateSession(worker, compilation, dispatcher);
        await using var session = created.Session;
        var documents = created.Documents;

        documents.SetSource("Program.cs", "A");
        var first = session.CompileAsync(storeInCache: true, updateDisplayedOutput: true);
        await transport.WaitStartedAsync(0).WaitAsync(TimeSpan.FromSeconds(2));

        documents.SetSource("Program.cs", "B");
        var middle = session.CompileAsync(storeInCache: true, updateDisplayedOutput: true);
        documents.SetSource("Program.cs", "C");
        var latest = session.CompileAsync(storeInCache: true, updateDisplayedOutput: true);

        transport.CompileCount.Should().Be(1);
        transport.Release(0);
        await transport.WaitStartedAsync(1).WaitAsync(TimeSpan.FromSeconds(2));
        transport.Texts.Should().Equal("A", "C");
        transport.Release(1);

        await Task.WhenAll(first, middle, latest).WaitAsync(TimeSpan.FromSeconds(2));
        FailText(session.Compiled).Should().Be("C");
    }

    [TestMethod]
    public async Task Compile_WaitsUntilCompilerIdleBeforeSending()
    {
        var transport = new DelayedCompileTransport();
        await using var worker = CreateWorker(transport);
        var compilation = new Store<CompilationState>(new CompilationState());
        var dispatcher = new RecordingDispatcher(compilation);
        var compiler = new Store<CompilerState>(new CompilerState { SdkLoading = true });
        var created = CreateSession(worker, compilation, dispatcher, compiler);
        await using var session = created.Session;

        var compile = session.CompileAsync(storeInCache: true, updateDisplayedOutput: true);
        await Task.Delay(80);
        compile.IsCompleted.Should().BeFalse();
        transport.CompileCount.Should().Be(0);

        compiler.Value = compiler.Value with { SdkLoading = false };
        await transport.WaitStartedAsync(0).WaitAsync(TimeSpan.FromSeconds(2));
        transport.CompileCount.Should().Be(1);
        transport.Release(0);
        await compile.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task CompileRequestedAction_EnqueuesOnExistingScheduler()
    {
        var transport = new DelayedCompileTransport();
        await using var worker = CreateWorker(transport);
        var compilation = new Store<CompilationState>(new CompilationState());
        var dispatcher = new RecordingDispatcher(compilation);
        var created = CreateSession(worker, compilation, dispatcher);
        await using var session = created.Session;
        var effects = new CompilationEffects(session);

        var compile = effects.Handle(new CompileRequestedAction(), dispatcher);
        await transport.WaitStartedAsync(0).WaitAsync(TimeSpan.FromSeconds(2));
        compilation.Value.Running.Should().BeTrue();

        transport.Release(0);
        await compile.WaitAsync(TimeSpan.FromSeconds(2));
        compilation.Value.Running.Should().BeFalse();
    }

    private static WorkerHost CreateWorker(IWorkerTransport transport)
        => new(
            new LabEnvironment(IsDevelopment: false, BaseAddress: "http://localhost/"),
            new LabLogging(),
            new LabSettings(new EmptyPrefsJsRuntime()),
            transport,
            NullLogger<WorkerHost>.Instance);

    private static (CompilationSession Session, LabDocuments Documents) CreateSession(
        WorkerHost worker,
        IState<CompilationState> compilation,
        IDispatcher dispatcher,
        IState<CompilerState>? compiler = null)
    {
        var documents = new LabDocuments(dispatcher, compilation);
        documents.SetSource("Program.cs", "class C;");
        var session = new CompilationSession(
            worker,
            new TemplateCache(),
            new NullCompilationCache(),
            compiler ?? new Store<CompilerState>(new CompilerState()),
            new Store<PreferencesState>(new PreferencesState { EnableCaching = false, LanguageServices = false }),
            compilation,
            new Store<CompilationOptionsState>(new CompilationOptionsState()),
            new Store<OutputState>(new OutputState()),
            dispatcher,
            NullLogger<CompilationSession>.Instance,
            documents,
            new LabEditorSnapshots());
        return (session, documents);
    }

    private static string FailText(CompiledAssembly? compiled)
        => compiled?.GetGlobalOutput("fail")?.Text
           ?? throw new InvalidOperationException("Missing fail output.");

    private sealed class DelayedCompileTransport : IWorkerTransport
    {
        private Action<string>? _onMessage;
        private readonly List<Gate> _gates = [];
        private readonly object _lock = new();

        public int CompileCount { get; private set; }

        public List<string> Texts { get; } = [];

        public Task WaitStartedAsync(int index) => GetGate(index).Started.Task;

        public void Release(int index) => GetGate(index).Release.TrySetResult();

        public Task EnsureControllerAsync() => Task.CompletedTask;

        public Task EnsureInProcessInteropAsync() => Task.CompletedTask;

        public bool SupportsBackgroundWorker => true;

        public IWorkerHandle CreateWorker(string scriptUrl, Action<string> onMessage, Action<string> onError)
        {
            _ = scriptUrl;
            _ = onError;
            _onMessage = onMessage;
            onMessage(Serialize(new WorkerOutputMessage.Ready
            {
                Id = WorkerOutputMessage.BroadcastId,
                InputType = WorkerOutputMessage.NoInputType,
            }));
            return new Handle();
        }

        public void WorkerReady(IWorkerHandle worker)
        {
        }

        public void PostMessage(IWorkerHandle worker, string message)
        {
            var incoming = JsonSerializer.Deserialize(message, WorkerJsonContext.Default.WorkerInputMessage);
            if (incoming is not WorkerInputMessage.Compile compile)
            {
                return;
            }

            var text = compile.Input.Inputs.Value[0].Text;
            int index;
            lock (_lock)
            {
                index = CompileCount;
                CompileCount++;
                Texts.Add(text);
            }

            var gate = GetGate(index);
            gate.Started.TrySetResult();
            _ = ReplyAsync(compile.Id, text, gate);
        }

        public void PostSideMessage(IWorkerHandle worker, string message)
        {
        }

        public void DisposeWorker(IWorkerHandle worker) => worker.Dispose();

        public void CollectAndDownloadGcDump()
        {
        }

        private Gate GetGate(int index)
        {
            lock (_lock)
            {
                while (_gates.Count <= index)
                {
                    _gates.Add(new Gate());
                }

                return _gates[index];
            }
        }

        private async Task ReplyAsync(int id, string text, Gate gate)
        {
            await gate.Release.Task;
            _onMessage!(Serialize(new WorkerOutputMessage.Success(CompiledAssembly.Fail(text))
            {
                Id = id,
                InputType = nameof(WorkerInputMessage.Compile),
            }));
        }

        private static string Serialize(WorkerOutputMessage message)
            => JsonSerializer.Serialize(message, WorkerJsonContext.Default.WorkerOutputMessage);

        private sealed class Gate
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class Handle : IWorkerHandle
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class Store<T>(T value) : IState<T>
    {
        public T Value { get; set; } = value;

        public event EventHandler StateChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class RecordingDispatcher(Store<CompilationState> compilation) : IDispatcher
    {
        public List<object> Actions { get; } = [];

        public event EventHandler<ActionDispatchedEventArgs> ActionDispatched
        {
            add { }
            remove { }
        }

        public void Dispatch(object action)
        {
            Actions.Add(action);
            if (action is SetRunningAction running)
            {
                compilation.Value = compilation.Value with { Running = running.Value };
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

    private sealed class ImmediateSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? _previous = Current;

        public static ImmediateSynchronizationContext Install()
        {
            var context = new ImmediateSynchronizationContext();
            SetSynchronizationContext(context);
            return context;
        }

        public override void Post(SendOrPostCallback d, object? state) => d(state);

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void Dispose() => SetSynchronizationContext(_previous);
    }

    private sealed class NullCompilationCache : ICompilationCache
    {
        public ValueTask<CachedCompilation?> GetAsync(SavedState state, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CachedCompilation?>(null);

        public Task StoreAsync(SavedState state, CompiledAssembly output, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
