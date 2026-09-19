using AwesomeAssertions;
using DotNetLab.Features.Compilation;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Features.Outputs;
using DotNetLab.Lab;
using Fluxor;

namespace DotNetLab;

[TestClass]
public sealed class OutputSessionTests
{
    [TestMethod]
    public void GetOutput_AsksToCompileWhenThereIsNoAssembly()
    {
        var (session, _) = Create();
        session.GetOutput("cs").Should().Be("(press Compile to load this)");
        session.IsEmpty.Should().BeTrue();
    }

    [TestMethod]
    public void GetOutput_ShowsCompilingPlaceholderWhileRunning()
    {
        var (session, host) = Create();
        host.Running = true;
        session.GetOutput("cs").Should().Be("Compiling…");
    }

    [TestMethod]
    public void GetOutput_UsesFailOutput()
    {
        var (session, host) = Create();
        host.Compiled = CompiledAssembly.Fail("boom");
        session.GetOutput("cs").Should().Be("boom");
    }

    [TestMethod]
    public void GetOutput_CachesEagerTextUntilCleared()
    {
        var (session, host) = Create();
        host.Compiled = AssemblyWithEager("cs", "class C;", "csharp");
        session.GetOutput("cs").Should().Be("class C;");
        session.OutputLanguage("cs").Should().Be("csharp");
        session.IsEmpty.Should().BeFalse();

        host.Compiled = null;
        session.GetOutput("cs").Should().Be("class C;");

        session.Clear();
        session.GetOutput("cs").Should().Be("(press Compile to load this)");
        session.IsEmpty.Should().BeTrue();
    }

    [TestMethod]
    public void GetOutput_MissingTabUsesLabel()
    {
        var (session, host) = Create();
        host.Compiled = AssemblyWithEager("il", ".class", "il");
        session.GetOutput("tree").Should().Be("(no Tree output for this file)");
    }

    [TestMethod]
    public void DisplayType_SwitchesToErrorListWhenOutputIsEmpty()
    {
        var (session, host) = Create();
        host.ActiveOutput = "cs";
        host.Compiled = AssemblyWithEager("cs", "", "csharp", errors: 1);
        session.SetTemporaryErrorList(true);
        session.DisplayType.Should().Be(LabCatalog.ErrorsOutputType);
        session.DismissTemporaryErrorList().Should().BeTrue();
        session.DisplayType.Should().Be("cs");
    }

    [TestMethod]
    public async Task EnsureOutputLoadedAsync_DropsStaleGeneration()
    {
        var (session, host) = Create();
        host.LastInput = new CompilationInput(new(ImmutableArray<InputCode>.Empty));
        host.CompileGeneration = 1;
        host.Compiled = new CompiledAssembly(
            Files: ImmutableSortedDictionary<string, CompiledFile>.Empty.Add(
                "Program.cs",
                new CompiledFile(
                [
                    new CompiledFileOutput
                    {
                        Type = "cs",
                        Label = "C#",
                        LazyText = async () =>
                        {
                            host.CompileGeneration++;
                            await Task.Yield();
                            return "late";
                        },
                    },
                ])),
            GlobalOutputs: [],
            NumWarnings: 0,
            NumErrors: 0,
            Diagnostics: [],
            BaseDirectory: "/");

        await session.EnsureOutputLoadedAsync("cs");
        session.IsEmpty.Should().BeTrue();
        host.StoredCount.Should().Be(0);
    }

    [TestMethod]
    public void GetOutput_WasmPluginRewritesUnavailableJit()
    {
        var (session, host) = Create(new WebAssemblyCompilerOutputPlugin());
        host.Compiled = AssemblyWithEager(
            "asm",
            "JIT disassembler is not available.",
            language: null,
            metadata: CompiledFileOutputMetadata.JitAsmUnavailableMessage);

        var text = session.GetOutput("asm");
        text.Should().Contain("native app");
        session.OutputLanguage("asm").Should().Be("plaintext");
        session.GetDisclaimer("asm").Should().Be(OutputDisclaimer.None);
    }

    [TestMethod]
    public void GetOutput_WasmPluginReusesNativeAsmAfterClear()
    {
        var (session, host) = Create(new WebAssemblyCompilerOutputPlugin());
        host.Compiled = AssemblyWithEager("asm", "mov eax, 1", "x86");
        session.GetOutput("asm").Should().Be("mov eax, 1");
        session.OutputLanguage("asm").Should().Be("x86");

        session.Clear();
        host.Compiled = AssemblyWithEager(
            "asm",
            "JIT disassembler is not available.",
            language: null,
            metadata: CompiledFileOutputMetadata.JitAsmUnavailableMessage);

        session.GetOutput("asm").Should().Be("mov eax, 1");
        session.OutputLanguage("asm").Should().Be("x86");
        session.GetDisclaimer("asm").Should().Be(OutputDisclaimer.JitAsmUnavailableUsingCached);
    }

    private static (OutputSession Session, Harness Host) Create(ICompilerOutputPlugin? plugin = null)
    {
        var host = new Harness(plugin);
        return (host.Session, host);
    }

    private static CompiledAssembly AssemblyWithEager(
        string type,
        string text,
        string? language,
        int errors = 0,
        CompiledFileOutputMetadata? metadata = null)
        => new(
            Files: ImmutableSortedDictionary<string, CompiledFile>.Empty.Add(
                "Program.cs",
                new CompiledFile(
                [
                    new CompiledFileOutput
                    {
                        Type = type,
                        Label = type == "cs" ? "C#" : type == "asm" ? "Asm" : "IL",
                        Language = language,
                        EagerText = text,
                        Metadata = metadata,
                    },
                ])),
            GlobalOutputs: [],
            NumWarnings: 0,
            NumErrors: errors,
            Diagnostics: [],
            BaseDirectory: "/");

    private sealed class Harness
    {
        private readonly OutputCompileState _compile = new();
        private readonly Store<CompilationState> _compilation = new(new CompilationState());
        private readonly Store<OutputState> _output = new(new OutputState());

        public Harness(ICompilerOutputPlugin? plugin = null)
        {
            var documents = new LabDocuments(new NoopDispatcher(), _compilation, _output);
            Session = new OutputSession(
                documents,
                _output,
                _compilation,
                plugin,
                compile: _compile);
        }

        public OutputSession Session { get; }

        public bool Running
        {
            set => _compilation.Value = _compilation.Value with { Running = value };
        }

        public string ActiveOutput
        {
            set => _output.Value = _output.Value with { ActiveOutput = value };
        }

        public CompiledAssembly? Compiled
        {
            get => _compile.Compiled;
            set => _compile.Compiled = value;
        }

        public CompilationInput? LastInput
        {
            get => _compile.LastInput;
            set => _compile.LastInput = value;
        }

        public int CompileGeneration
        {
            get => _compile.CompileGeneration;
            set => _compile.CompileGeneration = value;
        }

        public int StoredCount => _compile.StoredCount;
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

    private sealed class NoopDispatcher : IDispatcher
    {
        public event EventHandler<ActionDispatchedEventArgs> ActionDispatched
        {
            add { }
            remove { }
        }

        public void Dispatch(object action)
        {
        }
    }
}
