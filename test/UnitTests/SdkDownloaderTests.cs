using AwesomeAssertions;
using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab;

[TestClass]
public sealed class SdkDownloaderTests
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    [DataRow("9.0.300", "4.14.0-3.25218.8", "9.0.0-preview.25202.4")]
    [DataRow("10.0.100-preview.5.25277.114", "5.0.0-1.25272.4", "10.0.0-preview.25272.1")]
    [DataRow("10.0.100-preview.7.25351.106", "5.0.0-1.25351.106", "10.0.0-preview.25351.106")]
    [DataRow("10.0.100", "5.0.0-2.25523.111", "10.0.0-preview.25523.111")]
    public async Task CanDetermineSdkInfo(string version, string expectedRoslynVersion, string expectedRazorVersion)
    {
        var services = WorkerServices.CreateTest(TestContext);
        var downloader = services.GetRequiredService<SdkDownloader>();
        var info = await downloader.GetInfoAsync(version);
        (info.RoslynVersion, info.RazorVersion).Should().Be((expectedRoslynVersion, expectedRazorVersion));
    }

    [TestMethod]
    [DataRow("10.0.100-preview.7.25351.106", "20250701.6")]
    [DataRow("10.0.100-rtm.25523.111", "20251023.11")]
    [DataRow("10.0.100", null)]
    public void VersionToBuildNumber(string version, string? expectedBuildNumber)
    {
        VersionUtil.TryGetBuildNumberFromVersionNumber(version, out var actualBuildNumber)
            .Should().Be(expectedBuildNumber != null);
        actualBuildNumber.Should().Be(expectedBuildNumber);
    }
}
