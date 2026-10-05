using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Infrastructure.Worker;
using Fluxor;

namespace DotNetLab.Features.Outputs;

public sealed partial class OutputWorkspace
{
    private readonly DocumentWorkspace _documents;
    private readonly IState<OutputState> _output;
    private readonly IState<CompilationState> _compilation;
    private readonly IDispatcher _dispatcher;
    private readonly ICompilerOutputPlugin _plugin;
    private readonly CompilationSession? _compilationSession;
    private readonly WorkerHost? _worker;
    private readonly OutputCompileState? _compile;
    private readonly Dictionary<string, OutputSnapshot> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _modelUris = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    private readonly Dictionary<DocumentKind, List<string>> _outputTabOrder = CreateDefaultOutputTabOrder();
    private readonly Dictionary<DocumentKind, HashSet<string>> _hiddenOutputTabs = CreateDefaultHiddenOutputTabs();
    private readonly Dictionary<DocumentKind, List<string>> _openOutputTabs = new();
    private OutputSnapshot? _cachedNativeAsm;
    private DocumentKind? _syncedOutputKind;
    private bool _showErrorListIfOutputEmpty;

    public OutputWorkspace(
        DocumentWorkspace documents,
        IState<OutputState> output,
        IState<CompilationState> compilation,
        ICompilerOutputPlugin plugin,
        CompilationSession compilationSession,
        WorkerHost worker,
        IDispatcher dispatcher)
        : this(documents, output, compilation, dispatcher, plugin, compilationSession, worker, compile: null)
    {
    }

    internal OutputWorkspace(
        DocumentWorkspace documents,
        IState<OutputState> output,
        IState<CompilationState> compilation,
        IDispatcher dispatcher,
        ICompilerOutputPlugin? plugin = null,
        CompilationSession? compilationSession = null,
        WorkerHost? worker = null,
        OutputCompileState? compile = null)
    {
        _documents = documents;
        _output = output;
        _compilation = compilation;
        _dispatcher = dispatcher;
        _plugin = plugin ?? PassThroughCompilerOutputPlugin.Instance;
        _compilationSession = compilationSession;
        _worker = worker;
        _compile = compile;
        documents.Changed += EnsureActiveOutput;
        if (compilationSession is not null)
        {
            compilationSession.NewOutputGeneration += Clear;
        }
    }

    public event Action? Changed;

    public int Revision { get; private set; }

    public bool ShowRenderedHtml { get; private set; }

    public string DisplayType
        => _showErrorListIfOutputEmpty && HasEmptyOutputText(ActiveOutput) == true
            ? OutputCatalog.ErrorsId
            : ActiveOutput;

    private string ActiveDocument => _documents.ActiveDocument;

    private string ActiveOutput => _output.Value.ActiveOutput;

    private bool Running => _compilation.Value.Running;

    private CompiledAssembly? Compiled => _compilationSession?.Compiled ?? _compile?.Compiled;

    private CompilationInput? LastInput => _compilationSession?.LastInput ?? _compile?.LastInput;

    private bool StoreInCache => _compilationSession?.StoreInCache ?? _compile?.StoreInCache ?? false;

    private int CompileGeneration => _compilationSession?.CompileGeneration ?? _compile?.CompileGeneration ?? 0;

    private bool IsCurrentCompile(int generation)
        => _compilationSession?.IsCurrentCompile(generation)
            ?? generation == (_compile?.CompileGeneration ?? 0);

    private void Notify() => Changed?.Invoke();

    public OutputDefinition Get(string id) => OutputCatalog.Require(id);

    public void SetShowRenderedHtml(bool value)
    {
        if (ShowRenderedHtml == value)
        {
            return;
        }

        ShowRenderedHtml = value;
        Notify();
    }
}
