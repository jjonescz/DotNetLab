using AwesomeAssertions;
using DotNetLab.Features.Preferences;

namespace DotNetLab;

/// <summary>
/// Mapping of pre-redesign SettingsService localStorage keys into SettingsSnapshot.
/// </summary>
[TestClass]
public sealed class LegacySettingsTests
{
    [TestMethod]
    public void TryCreate_Empty_ReturnsNull()
    {
        LegacySettings.TryCreate(null).Should().BeNull();
        LegacySettings.TryCreate(new Dictionary<string, string?>()).Should().BeNull();
        LegacySettings.TryCreate(new Dictionary<string, string?> { ["WordWrap"] = "nope" }).Should().BeNull();
    }

    [TestMethod]
    public void TryCreate_MapsLegacyKeys()
    {
        var snapshot = LegacySettings.TryCreate(new Dictionary<string, string?>
        {
            [LegacySettings.WordWrapKey] = "true",
            [LegacySettings.UseVimKey] = "false",
            [LegacySettings.DebugLogsKey] = "true",
            [LegacySettings.TraceLogsKey] = "true",
            [LegacySettings.MemoryUsageViewKey] = "true",
            [LegacySettings.LanguageServicesKey] = "false",
            [LegacySettings.BackgroundWorkerKey] = "false",
            [LegacySettings.EnableCachingKey] = "false",
            [LegacySettings.AutomaticCompilationKey] = "false",
            [LegacySettings.DisplayHintSquigglesKey] = "true",
            [LegacySettings.DisableInputVirtualKeyboardKey] = "true",
            [LegacySettings.CompilationPreferencesKey] = """{"showOperations":true,"excludeSingleFileNameInDiagnostics":false}""",
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
        var snapshot = LegacySettings.TryCreate(new Dictionary<string, string?>
        {
            [LegacySettings.WordWrapKey] = "true",
            [LegacySettings.UseVimKey] = "maybe",
            [LegacySettings.CompilationPreferencesKey] = "{",
        });

        snapshot.Should().NotBeNull();
        snapshot!.WordWrap.Should().BeTrue();
        snapshot.UseVim.Should().BeNull();
        snapshot.CompilationPreferences.Should().BeNull();
    }
}
