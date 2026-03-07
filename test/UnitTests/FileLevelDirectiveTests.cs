using AwesomeAssertions;
using System.Runtime.InteropServices;

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
        var currentMajorVersion = RuntimeInformation.FrameworkDescription.Split(' ')[1].Split('.')[0];
        actualSuggestedValues.Should().Contain($"net{currentMajorVersion}.0");
    }

    [TestMethod]
    public void WarningLevel()
    {
        // Warning level should be from 0 to currrent .NET version. It should also suggest 9999 which is commonly used as the max value.
        var actualSuggestedValues = FileLevelDirective.Property.Descriptor
            .SuggestValues("WarningLevel", "")
            .Select(f => f.Split('=')[0]);
        var currentMajorVersion = int.Parse(RuntimeInformation.FrameworkDescription.Split(' ')[1].Split('.')[0]);
        var expectedSuggestedValues = Enumerable.Range(0, currentMajorVersion + 1).Concat([9999]).Select(i => i.ToString());
        actualSuggestedValues.Should().Equal(expectedSuggestedValues);
    }
}
