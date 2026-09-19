using AwesomeAssertions;
using DotNetLab.Features.Documents;

namespace DotNetLab;

[TestClass]
public sealed class DocumentMetadataStateTests
{
    [TestMethod]
    public void Reduce_UpdatesTemplateActiveAndOpenNames()
    {
        var next = DocumentMetadataReducers.Reduce(
            new DocumentMetadataState(),
            new SetDocumentMetadataAction("Razor", "TestComponent.razor", ["TestComponent.razor", "_Imports.razor"]));

        next.Template.Should().Be("Razor");
        next.ActiveDocument.Should().Be("TestComponent.razor");
        next.OpenNames.Should().Equal("TestComponent.razor", "_Imports.razor");
    }

    [TestMethod]
    public void Reduce_SameFacts_ReturnsSameInstance()
    {
        var state = new DocumentMetadataState();
        var next = DocumentMetadataReducers.Reduce(
            state,
            new SetDocumentMetadataAction(state.Template, state.ActiveDocument, state.OpenNames));

        next.Should().BeSameAs(state);
    }
}
