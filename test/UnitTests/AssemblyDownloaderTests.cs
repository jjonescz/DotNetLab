using AwesomeAssertions;
using DotNetLab.Lab;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;

namespace DotNetLab;

[TestClass]
public sealed class AssemblyDownloaderTests
{
    [TestMethod]
    public async Task DownloadsDllWhenHostServesDlls()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var downloader = new AssemblyDownloader(
            client,
            static () => null,
            Options.Create(new CompilerProxyOptions { AssembliesAreAlwaysInDllFormat = true }));

        var downloaded = await downloader.DownloadAsync("Microsoft.CodeAnalysis.CSharp");

        handler.Path.Should().Be("/_framework/Microsoft.CodeAnalysis.CSharp.dll");
        downloaded.Format.Should().Be(AssemblyDataFormat.Dll);
        downloaded.Data.Should().Equal(1, 2, 3);
    }

    [TestMethod]
    public async Task DownloadsWasmWhenWebcilIsEnabled()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var downloader = new AssemblyDownloader(
            client,
            static () => null,
            Options.Create(new CompilerProxyOptions()));

        var downloaded = await downloader.DownloadAsync("Microsoft.CodeAnalysis.CSharp");

        handler.Path.Should().Be("/_framework/Microsoft.CodeAnalysis.CSharp.wasm");
        downloaded.Format.Should().Be(AssemblyDataFormat.Webcil);
    }

    [TestMethod]
    public async Task PrefersBootConfigDllOverWasmFallback()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var downloader = new AssemblyDownloader(
            client,
            static () => new DotNetBootConfig
            {
                Resources = new()
                {
                    Assembly =
                    [
                        new()
                        {
                            VirtualPath = "Microsoft.CodeAnalysis.CSharp.dll",
                            Name = "Microsoft.CodeAnalysis.CSharp.abc123.dll",
                        },
                    ],
                },
            },
            Options.Create(new CompilerProxyOptions()));

        var downloaded = await downloader.DownloadAsync("Microsoft.CodeAnalysis.CSharp");

        handler.Path.Should().Be("/_framework/Microsoft.CodeAnalysis.CSharp.abc123.dll");
        downloaded.Format.Should().Be(AssemblyDataFormat.Dll);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(response);
        }
    }
}
