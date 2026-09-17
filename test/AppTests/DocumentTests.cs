using AwesomeAssertions;
using DotNetLab.Features.Documents;
using DotNetLab.Lab;

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
        var (documents, host) = Create();
        documents.Template.Should().Be("C#");
        documents.ActiveSource.Should().Be("Program.cs");
        documents.SourceFiles.Should().Equal("Program.cs");
        documents.Sources["Program.cs"].Should().Be(InitialCode.CSharp.TextTemplate);
        host.ActiveOutput.Should().Be("cs");
    }

    [TestMethod]
    public async Task SetTemplate_RazorReplacesUserFiles()
    {
        var (documents, host) = Create();
        documents.SetTemplate("Razor");
        await host.LastAfterDocumentsChanged!;

        documents.Template.Should().Be("Razor");
        documents.ActiveSource.Should().Be("TestComponent.razor");
        documents.SourceFiles.Should().Equal("TestComponent.razor", "_Imports.razor");
        host.ActiveOutput.Should().Be("gcs");
        host.Stale.Should().BeTrue();
        host.PersistCount.Should().Be(1);
    }

    [TestMethod]
    public void SetTemplate_Cshtml()
    {
        var (documents, host) = Create();
        documents.SetTemplate("CSHTML");
        documents.ActiveSource.Should().Be("TestPage.cshtml");
        documents.SourceFiles.Should().Equal("TestPage.cshtml");
        host.ActiveOutput.Should().Be("gcs");
    }

    [TestMethod]
    public void AddAndCloseFile()
    {
        var (documents, host) = Create();
        documents.AddFile(".cs");
        documents.ActiveSource.Should().Be("File1.cs");
        documents.SourceFiles.Should().Equal("Program.cs", "File1.cs");
        host.PersistCount.Should().Be(1);

        documents.CloseFile("Program.cs");
        documents.ActiveSource.Should().Be("File1.cs");
        documents.SourceFiles.Should().Equal("File1.cs");

        documents.CloseFile("File1.cs");
        documents.SourceFiles.Should().Equal("File1.cs");
    }

    [TestMethod]
    public void RenameFile_AcceptsAndRejects()
    {
        var (documents, host) = Create();
        documents.RenameFile("Program.cs", "Hello.cs");
        documents.ActiveSource.Should().Be("Hello.cs");
        documents.SourceFiles.Should().Equal("Hello.cs");
        host.PersistCount.Should().Be(1);

        documents.RenameFile("Hello.cs", "..");
        documents.SourceFiles.Should().Equal("Hello.cs");

        documents.OpenDirectives();
        documents.SourceFiles.Should().Equal("Hello.cs", "Directives.cs");
        documents.RenameFile("Directives.cs", "Other.cs");
        documents.SourceFiles.Should().Equal("Hello.cs", "Directives.cs");
    }

    [TestMethod]
    public void SetSource_MarksStaleOnce()
    {
        var (documents, host) = Create();
        host.Stale = false;
        documents.SetSource("Program.cs", "class C;");
        host.Stale.Should().BeTrue();

        documents.SetSource("Program.cs", "class D;");
        documents.Sources["Program.cs"].Should().Be("class D;");
    }

    [TestMethod]
    public void SetTemplate_RaisesChanged_SetSourceDoesNot()
    {
        var (documents, _) = Create();
        var count = 0;
        documents.Changed += () => count++;

        documents.SetSource("Program.cs", "class C;");
        count.Should().Be(0);

        documents.SetTemplate("Razor");
        count.Should().Be(1);
    }

    [TestMethod]
    public void LoadFromSavedState_InfersRazorTemplate()
    {
        var (documents, _) = Create();
        documents.LoadFromSavedState(SavedState.Razor);
        documents.Template.Should().Be("Razor");
        documents.ActiveSource.Should().Be("TestComponent.razor");
        documents.SourceFiles.Should().Equal("TestComponent.razor", "_Imports.razor");
    }

    [TestMethod]
    public void LoadImportedFiles_ReplacesUserFiles()
    {
        var (documents, host) = Create();
        documents.LoadImportedFiles(new Dictionary<string, string>
        {
            ["sub/App.cs"] = "class App;",
            [".."] = "ignored",
        });
        documents.SourceFiles.Should().Equal("App.cs");
        documents.Sources["App.cs"].Should().Be("class App;");
        documents.ActiveSource.Should().Be("App.cs");
        host.Stale.Should().BeTrue();
        host.PersistCount.Should().Be(1);
    }

    private static (LabDocuments Documents, FakeDocumentWorkspace Host) Create()
    {
        var host = new FakeDocumentWorkspace();
        return (new LabDocuments(host), host);
    }

    private sealed class FakeDocumentWorkspace : IDocumentWorkspace
    {
        public string ActiveOutput { get; set; } = "cs";
        public bool Stale { get; set; }
        public int PersistCount { get; private set; }
        public Task? LastAfterDocumentsChanged { get; private set; }

        public void EnsureActiveOutput()
        {
        }

        public void Notify()
        {
        }

        public Task AfterDocumentsChangedAsync(IReadOnlyList<string> before)
        {
            LastAfterDocumentsChanged = Task.CompletedTask;
            _ = before;
            return LastAfterDocumentsChanged;
        }

        public Task PersistUrlAsync(bool snapshot = false)
        {
            PersistCount++;
            return Task.CompletedTask;
        }

        public void AfterActiveSourceChanged()
        {
            PersistCount++;
        }

        public void PublishDocumentMetadata()
        {
        }
    }
}
