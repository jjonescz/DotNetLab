using System.Net;
using System.Text;
using AwesomeAssertions;
using DotNetLab.Features.Compiler;
using DotNetLab.Lab;

namespace DotNetLab;

[TestClass]
public sealed class CommitInfoLookupTests
{
    [TestMethod]
    public async Task TryGetAsync_UsesFirstLineDateAndCaches()
    {
        var handler = new StubHandler(
            """{"commit":{"author":{"date":"2026-09-20T21:00:00Z"},"message":"Fix hover\n\nDetails"}}""");
        using var client = new HttpClient(handler);
        var commit = new CommitLink
        {
            RepoUrl = "https://github.com/jjonescz/DotNetLab",
            Hash = "abc1234date",
        };

        var info = await CommitInfoLookup.TryGetAsync(client, commit);
        info.Should().NotBeNull();
        info!.Message.Should().Be("Fix hover");
        info.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture).Should().Be("2026-09-20");
        handler.Calls.Should().Be(1);

        (await CommitInfoLookup.TryGetAsync(client, commit)).Should().Be(info);
        handler.Calls.Should().Be(1);
    }

    [TestMethod]
    public async Task TryGetAsync_MissingOwner_ReturnsNull()
    {
        var handler = new StubHandler("{}");
        using var client = new HttpClient(handler);
        var commit = new CommitLink { RepoUrl = "https://example.test/repo", Hash = "abc1234" };

        var info = await CommitInfoLookup.TryGetAsync(client, commit);
        info.Should().BeNull();
        handler.Calls.Should().Be(0);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            request.RequestUri!.Host.Should().Be("api.github.com");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
