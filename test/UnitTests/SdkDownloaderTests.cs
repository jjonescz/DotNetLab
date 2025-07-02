using AwesomeAssertions;
using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab;

public sealed class SdkDownloaderTests
{
    [Theory]
    [InlineData("9.0.300", "4.14.0-3.25218.8", "9.0.0-preview.25202.4")]
    [InlineData("10.0.100-preview.5.25277.114", "5.0.0-1.25272.4", "10.0.0-preview.25272.1")]
    public async Task CanDetermineSdkInfo(string version, string expectedRoslynVersion, string expectedRazorVersion)
    {
        var services = WorkerServices.CreateTest();
        var downloader = services.GetRequiredService<SdkDownloader>();
        var info = await downloader.GetInfoAsync(version);
        (info.RoslynVersion, info.RazorVersion).Should().Be((expectedRoslynVersion, expectedRazorVersion));
    }
}
