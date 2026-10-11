using AwesomeAssertions;
using DotNetLab.Features.Preferences;
using DotNetLab.Shell.CommandPalette;

namespace DotNetLab;

[TestClass]
public sealed class DialogUiReducerTests
{
    [TestMethod]
    public void Settings_OpenAndClose_AreIdempotent()
    {
        var closed = new SettingsDialogState();
        var opened = SettingsDialogReducers.Reduce(closed, new OpenSettingsAction());
        opened.IsOpen.Should().BeTrue();
        SettingsDialogReducers.Reduce(opened, new OpenSettingsAction()).Should().BeSameAs(opened);

        var closedAgain = SettingsDialogReducers.Reduce(opened, new CloseSettingsAction());
        closedAgain.IsOpen.Should().BeFalse();
        SettingsDialogReducers.Reduce(closedAgain, new CloseSettingsAction()).Should().BeSameAs(closedAgain);
    }

    [TestMethod]
    public void CommandPalette_Toggle_Flips_And_Close_IsIdempotent()
    {
        var closed = new CommandPaletteState();
        var opened = CommandPaletteReducers.Reduce(closed, new ToggleCommandPaletteAction());
        opened.IsOpen.Should().BeTrue();
        CommandPaletteReducers.Reduce(opened, new ToggleCommandPaletteAction()).IsOpen.Should().BeFalse();

        var closedAgain = CommandPaletteReducers.Reduce(opened, new CloseCommandPaletteAction());
        closedAgain.IsOpen.Should().BeFalse();
        CommandPaletteReducers.Reduce(closedAgain, new CloseCommandPaletteAction()).Should().BeSameAs(closedAgain);
    }
}
