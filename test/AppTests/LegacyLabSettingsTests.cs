using AwesomeAssertions;
using DotNetLab.Features.Preferences;

namespace DotNetLab;

/// <summary>
/// Mapping of pre-redesign SettingsService localStorage keys into LabSettingsSnapshot.
/// </summary>
[TestClass]
public sealed class LegacyLabSettingsTests
{
    [TestMethod]
    public void TryCreate_Empty_ReturnsNull()
    {
        LegacyLabSettings.TryCreate(null).Should().BeNull();
        LegacyLabSettings.TryCreate(new Dictionary<string, string?>()).Should().BeNull();
        LegacyLabSettings.TryCreate(new Dictionary<string, string?> { ["WordWrap"] = "nope" }).Should().BeNull();
    }

    [TestMethod]
    public void TryCreate_MapsLegacyKeys()
    {
        var snapshot = LegacyLabSettings.TryCreate(new Dictionary<string, string?>
        {
            [LegacyLabSettings.WordWrapKey] = "true",
            [LegacyLabSettings.UseVimKey] = "false",
            [LegacyLabSettings.DebugLogsKey] = "true",
            [LegacyLabSettings.TraceLogsKey] = "true",
            [LegacyLabSettings.MemoryUsageViewKey] = "true",
            [LegacyLabSettings.LanguageServicesKey] = "false",
            [LegacyLabSettings.BackgroundWorkerKey] = "false",
            [LegacyLabSettings.EnableCachingKey] = "false",
            [LegacyLabSettings.AutomaticCompilationKey] = "false",
            [LegacyLabSettings.DisplayHintSquigglesKey] = "true",
            [LegacyLabSettings.DisableInputVirtualKeyboardKey] = "true",
            [LegacyLabSettings.CompilationPreferencesKey] = """{"showOperations":true,"excludeSingleFileNameInDiagnostics":false}""",
        });

        snapshot.Should().NotBeNull();
        snapshot!.WordWrap.Should().BeTrue();
        snapshot.UseVim.Should().BeFalse();
        snapshot.DebugLogs.Should().BeTrue();
        snapshot.TraceLogs.Should().BeTrue();
        snapshot.MemoryUsageView.Should().BeTrue();
        snapshot.LanguageServices.Should().BeFalse();
        snapshot.BackgroundWorker.Should().BeFalse();
        snapshot.EnableCaching.Should().BeFalse();
        snapshot.AutomaticCompilation.Should().BeFalse();
        snapshot.DisplayHintSquiggles.Should().BeTrue();
        snapshot.DisableInputVirtualKeyboard.Should().BeTrue();
        snapshot.CompilationPreferences.Should().NotBeNull();
        snapshot.CompilationPreferences!.ShowOperations.Should().BeTrue();
        snapshot.CompilationPreferences.ExcludeSingleFileNameInDiagnostics.Should().BeFalse();
        snapshot.CompilationPreferences.ShowSymbolKinds.Should().Be(SymbolDisplayKinds.None);
    }

    [TestMethod]
    public void TryCreate_SkipsInvalidValues()
    {
        var snapshot = LegacyLabSettings.TryCreate(new Dictionary<string, string?>
        {
            [LegacyLabSettings.WordWrapKey] = "true",
            [LegacyLabSettings.UseVimKey] = "maybe",
            [LegacyLabSettings.CompilationPreferencesKey] = "{",
        });

        snapshot.Should().NotBeNull();
        snapshot!.WordWrap.Should().BeTrue();
        snapshot.UseVim.Should().BeNull();
        snapshot.CompilationPreferences.Should().BeNull();
    }
}
