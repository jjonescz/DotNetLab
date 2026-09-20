using AwesomeAssertions;
using DotNetLab.Features.Preferences;
using DotNetLab.Lab;

namespace DotNetLab;

/// <summary>
/// Mapping of SettingsService localStorage keys into SettingsSnapshot.
/// </summary>
[TestClass]
public sealed class SettingsStorageSchemaTests
{
    [TestMethod]
    public void Read_Empty_ReturnsNull()
    {
        SettingsStorageSchema.Read(null).Should().BeNull();
        SettingsStorageSchema.Read(new Dictionary<string, string?>()).Should().BeNull();
        SettingsStorageSchema.Read(new Dictionary<string, string?> { ["WordWrap"] = "nope" }).Should().BeNull();
    }

    [TestMethod]
    public void Read_MapsEstablishedKeys()
    {
        var snapshot = SettingsStorageSchema.Read(new Dictionary<string, string?>
        {
            [SettingsStorageSchema.WordWrapKey] = "true",
            [SettingsStorageSchema.UseVimKey] = "false",
            [SettingsStorageSchema.DebugLogsKey] = "true",
            [SettingsStorageSchema.TraceLogsKey] = "true",
            [SettingsStorageSchema.MemoryUsageViewKey] = "true",
            [SettingsStorageSchema.LanguageServicesKey] = "false",
            [SettingsStorageSchema.BackgroundWorkerKey] = "false",
            [SettingsStorageSchema.EnableCachingKey] = "false",
            [SettingsStorageSchema.AutomaticCompilationKey] = "false",
            [SettingsStorageSchema.DisplayHintSquigglesKey] = "true",
            [SettingsStorageSchema.DisableInputVirtualKeyboardKey] = "true",
            [SettingsStorageSchema.CompilationPreferencesKey] = """{"showOperations":true,"excludeSingleFileNameInDiagnostics":false}""",
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
    public void Read_SkipsInvalidValues()
    {
        var snapshot = SettingsStorageSchema.Read(new Dictionary<string, string?>
        {
            [SettingsStorageSchema.WordWrapKey] = "true",
            [SettingsStorageSchema.UseVimKey] = "maybe",
            [SettingsStorageSchema.CompilationPreferencesKey] = "{",
        });

        snapshot.Should().NotBeNull();
        snapshot!.WordWrap.Should().BeTrue();
        snapshot.UseVim.Should().BeNull();
        snapshot.CompilationPreferences.Should().BeNull();
    }

    [TestMethod]
    public void Write_UsesJsonTrueFalseAndEstablishedKeys()
    {
        var written = SettingsStorageSchema.Write(new SettingsSnapshot
        {
            WordWrap = true,
            UseVim = false,
            LanguageServices = true,
            CompilationPreferences = new CompilationPreferences { ShowOperations = true },
        });

        written.Keys.Should().BeEquivalentTo(
        [
            SettingsStorageSchema.WordWrapKey,
            SettingsStorageSchema.UseVimKey,
            SettingsStorageSchema.LanguageServicesKey,
            SettingsStorageSchema.CompilationPreferencesKey,
        ]);
        written[SettingsStorageSchema.WordWrapKey].Should().Be("true");
        written[SettingsStorageSchema.UseVimKey].Should().Be("false");
        written[SettingsStorageSchema.LanguageServicesKey].Should().Be("true");
        written[SettingsStorageSchema.CompilationPreferencesKey].Should().Contain("\"showOperations\":true");

        var roundTrip = SettingsStorageSchema.Read(written);
        roundTrip.Should().NotBeNull();
        roundTrip!.WordWrap.Should().BeTrue();
        roundTrip.UseVim.Should().BeFalse();
        roundTrip.LanguageServices.Should().BeTrue();
        roundTrip.CompilationPreferences!.ShowOperations.Should().BeTrue();
    }
}
