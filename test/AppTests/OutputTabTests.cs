using AwesomeAssertions;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Features.Outputs;
using Fluxor;

namespace DotNetLab;

[TestClass]
public sealed class OutputTabTests
{
    [TestMethod]
    public void Catalog_KindOrderAndLock()
    {
        OutputCatalog.KindFor("Program.cs").Should().Be(OutputFileKind.Cs);
        OutputCatalog.KindFor("TestComponent.razor").Should().Be(OutputFileKind.Razor);
        OutputCatalog.KindFor("TestPage.cshtml").Should().Be(OutputFileKind.Cshtml);
        OutputCatalog.IsLocked("errors").Should().BeTrue();
        OutputCatalog.IsLocked("tree").Should().BeFalse();
        OutputCatalog.DefaultOrder(OutputFileKind.Cs).Should().Equal(
            "tree", "il", "seq", "cs", "asm", "xml", "run", "errors");
        OutputCatalog.DefaultOrder(OutputFileKind.Razor).Should().StartWith(
            ["syntax", "ir", "razorErrors", "gcs", "html"]);
        OutputCatalog.ProducedTypes("Program.cs").Should().Contain(["tree", "cs", "errors"]);
        OutputCatalog.ProducedTypes("Program.cs").Should().NotContain("razorErrors");
        OutputCatalog.ProducedTypes("TestComponent.razor").Should().Contain("gcs");
        OutputCatalog.ProducedTypes("TestComponent.razor").Should().NotContain("razorErrors");
    }

    [TestMethod]
    public void HideMoveAndReset()
    {
        var (tabs, host) = Create();
        tabs.OpenIds.Should().Contain("tree");
        tabs.SetOutputTabVisible(OutputFileKind.Cs, "tree", false);
        tabs.IsOutputTabVisible(OutputFileKind.Cs, "tree").Should().BeFalse();
        tabs.OpenIds.Should().NotContain("tree");
        tabs.OpenIds.Should().Contain("errors");

        tabs.SetOutputTabVisible(OutputFileKind.Cs, "errors", false);
        tabs.IsOutputTabVisible(OutputFileKind.Cs, "errors").Should().BeTrue();

        tabs.CanMoveOutputTab(OutputFileKind.Cs, "il", -1).Should().BeTrue();
        tabs.MoveOutputTab(OutputFileKind.Cs, "il", -1);
        tabs.SettingsRowsFor(OutputFileKind.Cs).Select(tab => tab.Id).First().Should().Be("il");

        tabs.CanMoveOutputTab(OutputFileKind.Cs, "il", -1).Should().BeFalse();
        tabs.ResetOutputTabs(OutputFileKind.Cs);
        tabs.IsOutputTabVisible(OutputFileKind.Cs, "tree").Should().BeTrue();
        tabs.SettingsRowsFor(OutputFileKind.Cs).Select(tab => tab.Id).First().Should().Be("tree");
        host.ActiveOutput.Should().Be("cs");
    }

    [TestMethod]
    public void SerializeRoundTripPreservesHiddenTabs()
    {
        var (tabs, _) = Create();
        tabs.SetOutputTabVisible(OutputFileKind.Cs, "tree", false);
        var json = tabs.SerializeOutputTabs();

        var (restored, _) = Create();
        restored.ApplySavedOutputTabs(json);
        restored.IsOutputTabVisible(OutputFileKind.Cs, "tree").Should().BeFalse();
        restored.IsOutputTabVisible(OutputFileKind.Cs, "errors").Should().BeTrue();
    }

    [TestMethod]
    public void AddOutputTab_SelectsTheNewTab()
    {
        var (tabs, host) = Create();
        tabs.CaptureOpenOutputTabs(["cs", "errors"]);
        tabs.AddOutputTab("il");
        tabs.OpenIds.Should().Contain("il");
        host.ActiveOutput.Should().Be("il");
    }

    [TestMethod]
    public void ApplySavedOutputTabs_IgnoresGarbage()
    {
        var (tabs, _) = Create();
        tabs.ApplySavedOutputTabs("{not-json");
        tabs.IsOutputTabVisible(OutputFileKind.Cs, "tree").Should().BeTrue();
    }

    private static (OutputWorkspace Tabs, Harness Host) Create()
    {
        var host = new Harness();
        return (host.Tabs, host);
    }

    private sealed class Harness
    {
        public Harness()
        {
            var compilation = new Store<CompilationState>(new CompilationState());
            Output = new Store<OutputState>(new OutputState());
            var dispatcher = new RecordingDispatcher(Output);
            var documents = new LabDocuments(dispatcher, compilation);
            Tabs = new OutputWorkspace(documents, Output, compilation, dispatcher);
        }

        public OutputWorkspace Tabs { get; }

        public Store<OutputState> Output { get; }

        public string ActiveOutput => Output.Value.ActiveOutput;
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

    private sealed class RecordingDispatcher(Store<OutputState> output) : IDispatcher
    {
        public event EventHandler<ActionDispatchedEventArgs> ActionDispatched
        {
            add { }
            remove { }
        }

        public void Dispatch(object action)
        {
            if (action is SetActiveOutputAction active)
            {
                output.Value = OutputReducers.Reduce(output.Value, active);
            }
        }
    }
}
