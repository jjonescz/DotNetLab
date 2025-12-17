using AwesomeAssertions;
using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab;

public sealed class SdkDownloaderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("9.0.300", "4.14.0-3.25218.8", "9.0.0-preview.25202.4")]
    [InlineData("10.0.100-preview.5.25277.114", "5.0.0-1.25272.4", "10.0.0-preview.25272.1")]
    [InlineData("10.0.100-preview.7.25351.106", "5.0.0-1.25351.106", "10.0.0-preview.25351.106")]
    [InlineData("10.0.100", "5.0.0-2.25523.111", "10.0.0-preview.25523.111")]
    public async Task CanDetermineSdkInfo(string version, string expectedRoslynVersion, string expectedRazorVersion)
    {
        var services = WorkerServices.CreateTest(output);
        var downloader = services.GetRequiredService<SdkDownloader>();
        var info = await downloader.GetInfoAsync(version);
        (info.RoslynVersion, info.RazorVersion).Should().Be((expectedRoslynVersion, expectedRazorVersion));
    }

    [Theory]
    [InlineData("10.0.100-preview.7.25351.106", "20250701.6")]
    [InlineData("10.0.100-rtm.25523.111", "20251023.11")]
    [InlineData("10.0.100", null)]
    public void VersionToBuildNumber(string version, string? expectedBuildNumber)
    {
        VersionUtil.TryGetBuildNumberFromVersionNumber(version, out var actualBuildNumber)
            .Should().Be(expectedBuildNumber != null);
        actualBuildNumber.Should().Be(expectedBuildNumber);
    }
}
