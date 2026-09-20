using AwesomeAssertions;
using DotNetLab.Features.Documents;

namespace DotNetLab;

[TestClass]
public sealed class DocumentStateTests
{
    [TestMethod]
    public void Reduce_UpdatesTemplateActiveAndOpenNames()
    {
        var next = DocumentReducers.Reduce(
            new DocumentState(),
            new SetDocumentStateAction("Razor", "TestComponent.razor", ["TestComponent.razor", "_Imports.razor"]));

        next.Template.Should().Be("Razor");
        next.ActiveDocument.Should().Be("TestComponent.razor");
        next.OpenNames.Should().Equal("TestComponent.razor", "_Imports.razor");
    }

    [TestMethod]
    public void Reduce_SameFacts_ReturnsSameInstance()
    {
        var state = new DocumentState();
        var next = DocumentReducers.Reduce(
            state,
            new SetDocumentStateAction(state.Template, state.ActiveDocument, state.OpenNames));

        next.Should().BeSameAs(state);
    }
}
