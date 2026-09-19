using AwesomeAssertions;
using DotNetLab.Features.Theme;

namespace DotNetLab;

/// <summary>
/// <c>netlab-theme</c> stores <c>light</c>|<c>dark</c>|<c>system</c>; Fluent's
/// old <c>theme</c> key stores JSON like <c>{"mode":"light"}</c>.
/// </summary>
[TestClass]
public sealed class LabThemeTests
{
    [TestMethod]
    public void TryParseStoredValue_ReadsNewAndFluentTheme()
    {
        LabTheme.TryParseStoredValue("light").Should().Be("light");
        LabTheme.TryParseStoredValue("dark").Should().Be("dark");
        LabTheme.TryParseStoredValue("system").Should().Be("system");
        LabTheme.TryParseStoredValue("""{"mode":"light"}""").Should().Be("light");
        LabTheme.TryParseStoredValue("\"system\"").Should().Be("system");
        LabTheme.TryParseStoredValue("""{"mode":"nope"}""").Should().BeNull();
        LabTheme.TryParseStoredValue("nope").Should().BeNull();
        LabTheme.TryParseStoredValue(null).Should().BeNull();
    }

    [TestMethod]
    public void NormalizePreference_DefaultsToDark()
    {
        LabTheme.NormalizePreference(null).Should().Be("dark");
        LabTheme.NormalizePreference("system").Should().Be("system");
    }
}
