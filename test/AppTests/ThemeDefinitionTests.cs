using AwesomeAssertions;
using DotNetLab.Features.Preferences;

namespace DotNetLab;

/// <summary>
/// <c>netlab-theme</c> stores <c>light</c>|<c>dark</c>|<c>system</c>; Fluent's
/// old <c>theme</c> key stores JSON like <c>{"mode":"light"}</c>.
/// </summary>
[TestClass]
public sealed class ThemeDefinitionTests
{
    [TestMethod]
    public void TryParseStoredValue_ReadsNewAndFluentTheme()
    {
        ThemeDefinition.TryParseStoredValue("light").Should().Be("light");
        ThemeDefinition.TryParseStoredValue("dark").Should().Be("dark");
        ThemeDefinition.TryParseStoredValue("system").Should().Be("system");
        ThemeDefinition.TryParseStoredValue("""{"mode":"light"}""").Should().Be("light");
        ThemeDefinition.TryParseStoredValue("\"system\"").Should().Be("system");
        ThemeDefinition.TryParseStoredValue("""{"mode":"nope"}""").Should().BeNull();
        ThemeDefinition.TryParseStoredValue("nope").Should().BeNull();
        ThemeDefinition.TryParseStoredValue(null).Should().BeNull();
    }

    [TestMethod]
    public void NormalizePreference_DefaultsToDark()
    {
        ThemeDefinition.NormalizePreference(null).Should().Be("dark");
        ThemeDefinition.NormalizePreference("system").Should().Be("system");
    }
}
