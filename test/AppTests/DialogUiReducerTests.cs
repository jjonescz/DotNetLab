using AwesomeAssertions;
using DotNetLab.Features.Preferences;
using DotNetLab.Features.Sharing;
using DotNetLab.Shell.CommandPalette;

namespace DotNetLab;

[TestClass]
public sealed class DialogUiReducerTests
{
    [TestMethod]
    public void Settings_OpenAndClose_AreIdempotent()
    {
        var closed = new SettingsUiState();
        var opened = SettingsUiReducers.Reduce(closed, new OpenSettingsAction());
        opened.IsOpen.Should().BeTrue();
        SettingsUiReducers.Reduce(opened, new OpenSettingsAction()).Should().BeSameAs(opened);

        var closedAgain = SettingsUiReducers.Reduce(opened, new CloseSettingsAction());
        closedAgain.IsOpen.Should().BeFalse();
        SettingsUiReducers.Reduce(closedAgain, new CloseSettingsAction()).Should().BeSameAs(closedAgain);
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

    [TestMethod]
    public void PasteUrl_OpenAndClose_AreIdempotent()
    {
        var closed = new PasteUrlUiState();
        var opened = PasteUrlUiReducers.Reduce(closed, new OpenPasteUrlAction());
        opened.IsOpen.Should().BeTrue();
        PasteUrlUiReducers.Reduce(opened, new OpenPasteUrlAction()).Should().BeSameAs(opened);

        var closedAgain = PasteUrlUiReducers.Reduce(opened, new ClosePasteUrlAction());
        closedAgain.IsOpen.Should().BeFalse();
        PasteUrlUiReducers.Reduce(closedAgain, new ClosePasteUrlAction()).Should().BeSameAs(closedAgain);
    }
}
