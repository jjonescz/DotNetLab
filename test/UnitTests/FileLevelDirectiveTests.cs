using AwesomeAssertions;

namespace DotNetLab;

public sealed class FileLevelDirectiveTests
{
    private static IEnumerable<string> GetSuggestedFeatureFlagNames()
    {
        return FileLevelDirective.Property.Descriptor
            .SuggestValues("Features", "")
            .Select(f => f.Split('=')[0]);
    }

    [Fact]
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

    [Fact]
    public void CompilerFeatureFlags_Sorted()
    {
        var actualSuggestedValues = GetSuggestedFeatureFlagNames();

        var expectedSuggestedValues = actualSuggestedValues.Order();

        actualSuggestedValues.Should().Equal(expectedSuggestedValues);
    }
}
