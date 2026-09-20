using AwesomeAssertions;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Features.Outputs;
using DotNetLab.Features.Sharing;
using DotNetLab.Lab;
using Fluxor;

namespace DotNetLab;

[TestClass]
public sealed class DocumentTests
{
    [TestMethod]
    public void DisplayName_SpecialSources()
    {
        LabDocuments.IsSpecialSource("Directives.cs").Should().BeTrue();
        LabDocuments.IsSpecialSource("Configuration.cs").Should().BeTrue();
        LabDocuments.IsSpecialSource("Program.cs").Should().BeFalse();
        LabDocuments.DisplayName("Directives.cs").Should().Be("Directives");
        LabDocuments.DisplayName("Configuration.cs").Should().Be("Configuration");
        LabDocuments.DisplayName("Program.cs").Should().Be("Program.cs");
    }

    [TestMethod]
    public void StartsWithCSharpProgram()
    {
        var harness = Create();
        harness.Documents.Template.Should().Be("C#");
        harness.Documents.ActiveSource.Should().Be("Program.cs");
        harness.Documents.SourceFiles.Should().Equal("Program.cs");
        harness.Documents.Sources["Program.cs"].Should().Be(InitialCode.CSharp.TextTemplate);
        harness.Output.Value.ActiveOutput.Should().Be("cs");
    }

    [TestMethod]
    public void SetTemplate_RazorReplacesUserFiles()
    {
        var harness = Create();
        harness.Documents.SetTemplate("Razor");

        harness.Documents.Template.Should().Be("Razor");
        harness.Documents.ActiveSource.Should().Be("TestComponent.razor");
        harness.Documents.SourceFiles.Should().Equal("TestComponent.razor", "_Imports.razor");
        harness.Output.Value.ActiveOutput.Should().Be("gcs");
        harness.Compilation.Value.Stale.Should().BeTrue();
        harness.PersistCount.Should().Be(1);
    }

    [TestMethod]
    public void SetTemplate_Cshtml()
    {
        var harness = Create();
        harness.Documents.SetTemplate("CSHTML");
        harness.Documents.ActiveSource.Should().Be("TestPage.cshtml");
        harness.Documents.SourceFiles.Should().Equal("TestPage.cshtml");
        harness.Output.Value.ActiveOutput.Should().Be("gcs");
    }

    [TestMethod]
    public void AddAndCloseFile()
    {
        var harness = Create();
        harness.Documents.AddFile(".cs");
        harness.Documents.ActiveSource.Should().Be("File1.cs");
        harness.Documents.SourceFiles.Should().Equal("Program.cs", "File1.cs");
        harness.PersistCount.Should().Be(1);

        harness.Documents.CloseFile("Program.cs");
        harness.Documents.ActiveSource.Should().Be("File1.cs");
        harness.Documents.SourceFiles.Should().Equal("File1.cs");

        harness.Documents.CloseFile("File1.cs");
        harness.Documents.SourceFiles.Should().Equal("File1.cs");
    }

    [TestMethod]
    public void RenameFile_AcceptsAndRejects()
    {
        var harness = Create();
        harness.Documents.RenameFile("Program.cs", "Hello.cs");
        harness.Documents.ActiveSource.Should().Be("Hello.cs");
        harness.Documents.SourceFiles.Should().Equal("Hello.cs");
        harness.PersistCount.Should().Be(1);

        harness.Documents.RenameFile("Hello.cs", "..");
        harness.Documents.SourceFiles.Should().Equal("Hello.cs");

        harness.Documents.OpenDirectives();
        harness.Documents.SourceFiles.Should().Equal("Hello.cs", "Directives.cs");
        harness.Documents.RenameFile("Directives.cs", "Other.cs");
        harness.Documents.SourceFiles.Should().Equal("Hello.cs", "Directives.cs");
    }

    [TestMethod]
    public void SetSource_MarksStaleOnce()
    {
        var harness = Create();
        harness.Compilation.Value = harness.Compilation.Value with { Stale = false };
        harness.Documents.SetSource("Program.cs", "class C;");
        harness.Compilation.Value.Stale.Should().BeTrue();

        harness.Documents.SetSource("Program.cs", "class D;");
        harness.Documents.Sources["Program.cs"].Should().Be("class D;");
    }

    [TestMethod]
    public void SetTemplate_RaisesChanged_SetSourceDoesNot()
    {
        var harness = Create();
        var count = 0;
        harness.Documents.Changed += () => count++;

        harness.Documents.SetSource("Program.cs", "class C;");
        count.Should().Be(0);

        harness.Documents.SetTemplate("Razor");
        count.Should().Be(1);
    }

    [TestMethod]
    public void LoadFromSavedState_InfersRazorTemplate()
    {
        var harness = Create();
        harness.Documents.LoadFromSavedState(SavedState.Razor);
        harness.Documents.Template.Should().Be("Razor");
        harness.Documents.ActiveSource.Should().Be("TestComponent.razor");
        harness.Documents.SourceFiles.Should().Equal("TestComponent.razor", "_Imports.razor");
    }

    [TestMethod]
    public void LoadImportedFiles_ReplacesUserFiles()
    {
        var harness = Create();
        harness.Documents.LoadImportedFiles(new Dictionary<string, string>
        {
            ["sub/App.cs"] = "class App;",
            [".."] = "ignored",
        });
        harness.Documents.SourceFiles.Should().Equal("App.cs");
        harness.Documents.Sources["App.cs"].Should().Be("class App;");
        harness.Documents.ActiveSource.Should().Be("App.cs");
        harness.Compilation.Value.Stale.Should().BeTrue();
        harness.PersistCount.Should().Be(1);
    }

    private static Harness Create() => new();

    private sealed class Harness
    {
        public Harness()
        {
            Compilation = new Store<CompilationState>(new CompilationState());
            Output = new Store<OutputState>(new OutputState());
            Dispatcher = new RecordingDispatcher(Compilation, Output);
            Documents = new LabDocuments(Dispatcher, Compilation);
        }

        public LabDocuments Documents { get; }

        public Store<CompilationState> Compilation { get; }

        public Store<OutputState> Output { get; }

        public RecordingDispatcher Dispatcher { get; }

        public int PersistCount => Dispatcher.PersistCount;
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

    private sealed class RecordingDispatcher(Store<CompilationState> compilation, Store<OutputState> output) : IDispatcher
    {
        public event EventHandler<ActionDispatchedEventArgs> ActionDispatched
        {
            add { }
            remove { }
        }

        public int PersistCount { get; private set; }

        public void Dispatch(object action)
        {
            if (action is SetStaleAction stale)
            {
                compilation.Value = compilation.Value with { Stale = stale.Value };
            }
            else if (action is SetActiveOutputAction active)
            {
                output.Value = OutputReducers.Reduce(output.Value, active);
            }
            else if (action is PersistUrlAction)
            {
                PersistCount++;
            }
        }
    }
}
