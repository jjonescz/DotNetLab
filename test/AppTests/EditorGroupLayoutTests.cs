using AwesomeAssertions;
using DotNetLab.Features.Workspace;

namespace DotNetLab;

[TestClass]
public sealed class EditorGroupLayoutTests
{
    [TestMethod]
    public void SyncFromItems_AddsAndRemoves()
    {
        var layout = Create("Program.cs", "File1.cs");
        layout.SyncFromItems(["Program.cs"], "Program.cs");
        layout.CurrentTabIds().Should().Equal("Program.cs");
        layout.Groups.Should().HaveCount(1);
        layout.Groups[0].Active.Should().Be("Program.cs");

        layout.SyncFromItems(["Program.cs", "File2.cs"], "File2.cs");
        layout.CurrentTabIds().Should().Equal("Program.cs", "File2.cs");
        layout.Groups[0].Active.Should().Be("File2.cs");
    }

    [TestMethod]
    public void MoveTab_WithinAndAcrossGroups()
    {
        var layout = Create("A.cs", "B.cs", "C.cs");
        layout.MoveTab("source-1", "C.cs", "source-1", 0);
        layout.Groups[0].Items.Should().Equal("C.cs", "A.cs", "B.cs");
        layout.Groups[0].Active.Should().Be("C.cs");

        layout.SplitTab(DropZone.Right, "B.cs", "source-1", "source-1");
        layout.Groups.Should().HaveCount(2);
        layout.MoveTab(layout.Groups[1].Id, "B.cs", layout.Groups[0].Id, 1);
        layout.Groups.Should().HaveCount(1);
        layout.Groups[0].Items.Should().Equal("C.cs", "B.cs", "A.cs");
        layout.Groups[0].Active.Should().Be("B.cs");
    }

    [TestMethod]
    public void SplitTab_PlacesGroupsByZone()
    {
        var layout = Create("A.cs", "B.cs");
        layout.SplitTab(DropZone.Right, "B.cs", "source-1", "source-1");
        layout.Orientation.Should().Be("horizontal");
        layout.Groups.Select(group => group.Items.Single()).Should().Equal("A.cs", "B.cs");

        layout = Create("A.cs", "B.cs");
        layout.SplitTab(DropZone.Left, "B.cs", "source-1", "source-1");
        layout.Orientation.Should().Be("horizontal");
        layout.Groups.Select(group => group.Items.Single()).Should().Equal("B.cs", "A.cs");

        layout = Create("A.cs", "B.cs");
        layout.SplitTab(DropZone.Top, "B.cs", "source-1", "source-1");
        layout.Orientation.Should().Be("vertical");
        layout.Groups.Select(group => group.Items.Single()).Should().Equal("B.cs", "A.cs");

        layout = Create("A.cs", "B.cs");
        layout.SplitTab(DropZone.Bottom, "B.cs", "source-1", "source-1");
        layout.Orientation.Should().Be("vertical");
        layout.Groups.Select(group => group.Items.Single()).Should().Equal("A.cs", "B.cs");
    }

    [TestMethod]
    public void Close_LastInGroup_RemovesTheGroup()
    {
        var layout = Create("A.cs", "B.cs");
        layout.SplitTab(DropZone.Right, "B.cs", "source-1", "source-1");
        layout.Groups.Should().HaveCount(2);

        var result = layout.Close(layout.Groups[1].Id, "B.cs");
        result.Closed.Should().BeTrue();
        result.WasActive.Should().BeTrue();
        result.NextActive.Should().Be("A.cs");
        layout.Groups.Should().HaveCount(1);
        layout.CurrentTabIds().Should().Equal("A.cs");
    }

    [TestMethod]
    public void Rename_RejectsPinnedAndExisting()
    {
        var layout = Create("Program.cs", "Directives.cs");
        layout.PinnedOrder = ["Directives.cs"];
        var items = layout.CurrentTabIds();

        layout.CanApplyRename("Directives.cs", "Other.cs", items).Should().BeFalse();
        layout.TryApplyRename("Directives.cs", "Other.cs", items).Should().BeFalse();
        layout.CanApplyRename("Program.cs", "Directives.cs", items).Should().BeFalse();
        layout.CanApplyRename("Program.cs", "../x.cs", items).Should().BeFalse();
        layout.TryApplyRename("Program.cs", "App.cs", items).Should().BeTrue();
        layout.CurrentTabIds().Should().Equal("App.cs", "Directives.cs");
        layout.Groups[0].Active.Should().Be("App.cs");
    }

    [TestMethod]
    public void Close_RejectsLockedAndLastTab()
    {
        var layout = Create("cs", "errors");
        layout.LockedItems = ["errors"];

        layout.Close("source-1", "errors").Should().Be(EditorGroupCloseResult.Ignored);
        layout.CurrentTabIds().Should().Equal("cs", "errors");

        layout.Close("source-1", "cs").Closed.Should().BeTrue();
        layout.CurrentTabIds().Should().Equal("errors");
        layout.Close("source-1", "errors").Should().Be(EditorGroupCloseResult.Ignored);
    }

    private static EditorGroupLayout Create(params string[] items)
    {
        var layout = new EditorGroupLayout("source") { AllowClose = true };
        layout.TryInitialize(items, items[0]).Should().BeTrue();
        return layout;
    }
}
