using AwesomeAssertions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab;

[TestClass]
public sealed class FileLevelDirectiveTests
{
    private static IEnumerable<string> GetSuggestedFeatureFlagNames()
    {
        return FileLevelDirective.Property.Descriptor
            .SuggestValues("Features", "")
            .Select(f => f.Split('=')[0]);
    }

    [TestMethod]
    public void CompilerFeatureFlags_All()
    {
        var actualSuggestedValues = GetSuggestedFeatureFlagNames().Distinct();

        var expectedSuggestedValues = RoslynAccessors.GetFeatureNames().Except(
            [
                "Experiment",
                "Test",
            ]);

        actualSuggestedValues.Should().BeEquivalentTo(expectedSuggestedValues);
    }

    [TestMethod]
    public void CompilerFeatureFlags_Sorted()
    {
        var actualSuggestedValues = GetSuggestedFeatureFlagNames();

        var expectedSuggestedValues = actualSuggestedValues.Order();

        actualSuggestedValues.Should().Equal(expectedSuggestedValues);
    }

    [TestMethod]
    public void TargetFramework()
    {
        // The current target framework should be among the suggested values.
        var actualSuggestedValues = FileLevelDirective.Property.Descriptor
            .SuggestValues("TargetFramework", "")
            .Select(f => f.Split('=')[0]);
        actualSuggestedValues.Should().Contain($"net{Environment.Version.Major}.{Environment.Version.Minor}");
    }

    [TestMethod]
    public void WarningLevel()
    {
        // Warning level should be from 0 to current .NET version. It should also suggest 9999 which is commonly used as the max value.
        var actualSuggestedValues = FileLevelDirective.Property.Descriptor
            .SuggestValues("WarningLevel", "")
            .Select(f => f.Split('=')[0]);
        var expectedSuggestedValues = Enumerable.Range(0, Environment.Version.Major + 1).Concat([9999]).Select(i => i.ToString());
        actualSuggestedValues.Should().Equal(expectedSuggestedValues);
    }

    [TestMethod]
    public void RazorLangVersion_Suggestions()
    {
        FileLevelDirective.Property.Descriptor.SuggestNames("").Should().Contain("RazorLangVersion");

        var values = FileLevelDirective.Property.Descriptor.SuggestValues("razorlangversion", "");
        values.Should().Contain(["preview", "latest", "experimental", "11.0", "1.0"]);
        foreach (var value in values)
        {
            Assert.IsTrue(RazorLanguageVersion.TryParse(value, out _), value);
        }
    }

    [TestMethod]
    public void RazorLangVersion_Default()
    {
        Assert.AreEqual(RazorLanguageVersion.Preview, RazorUtil.DefaultLanguageVersion);
    }

    [TestMethod]
    public void RazorLangVersion_Configuration()
    {
        var original = RazorConfiguration.Default with
        {
            ConfigurationName = "Test",
            CSharpLanguageVersion = LanguageVersion.CSharp12,
            UseRoslynTokenizer = true,
        };

        var modified = original.WithLanguageVersionSafe(RazorLanguageVersion.Version_7_0);

        Assert.AreEqual(original with { LanguageVersion = RazorLanguageVersion.Version_7_0 }, modified);
        Assert.AreEqual(RazorConfiguration.Default.LanguageVersion, original.LanguageVersion);
    }

    [TestMethod]
    [DataRow("Preview", null)]
    [DataRow("pReViEw", null)]
    [DataRow("11.0", "11.0")]
    [DataRow("Latest", null)]
    [DataRow("5.0", "5.0")]
    [DataRow("Experimental", null)]
    public async Task RazorLangVersion_Valid(string value, string? expectedVersion)
    {
        var directives = FileLevelDirectiveParser.Instance.Parse(
            [new() { FileName = "Input.cs", Text = $"#:property RazorLangVersion={value}" }]);
        var context = new FileLevelDirective.ConsumerContext
        {
            Directives = directives,
            Services = new ServiceCollection().BuildServiceProvider(),
            Config = new ConfigCollector(),
        };

        await context.ConsumeAsync();

        expectedVersion ??= value.ToLowerInvariant() switch
        {
            "preview" => RazorLanguageVersion.Preview.ToString(),
            "latest" => RazorLanguageVersion.Latest.ToString(),
            "experimental" => RazorLanguageVersion.Experimental.ToString(),
            _ => throw new InvalidOperationException($"Missing expected version for '{value}'."),
        };
        Assert.AreEqual(expectedVersion, context.RazorLanguageVersion?.ToString());
        Assert.IsEmpty(directives.Single().Info.Errors);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("invalid")]
    [DataRow("13.0")]
    [DataRow("8")]
    public async Task RazorLangVersion_Invalid(string value)
    {
        var directives = FileLevelDirectiveParser.Instance.Parse(
            [new() { FileName = "Input.cs", Text = $"#:property RazorLangVersion={value}" }]);
        var context = new FileLevelDirective.ConsumerContext
        {
            Directives = directives,
            Services = new ServiceCollection().BuildServiceProvider(),
            Config = new ConfigCollector(),
        };

        await context.ConsumeAsync();

        Assert.IsNull(context.RazorLanguageVersion);
        directives.Single().Info.Errors.Should().Equal($"Invalid property value '{value}'.");
    }
}
