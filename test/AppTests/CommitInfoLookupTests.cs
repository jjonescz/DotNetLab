using System.Net;
using System.Text;
using AwesomeAssertions;
using DotNetLab.Infrastructure.GitHub;
using DotNetLab.Lab;

namespace DotNetLab;

[TestClass]
public sealed class CommitInfoLookupTests
{
    [TestMethod]
    public async Task TryGetAsync_UsesFirstLineDateAndCaches()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"commit":{"author":{"date":"2026-09-20T21:00:00Z"},"message":"Fix hover\n\nDetails"}}""");
        using var client = new HttpClient(handler);
        var lookup = new CommitInfoLookup(client);
        var commit = new CommitLink
        {
            RepoUrl = "https://github.com/jjonescz/DotNetLab",
            Hash = "abc1234date",
        };

        var info = await lookup.TryGetAsync(commit);
        info.Should().NotBeNull();
        info!.Message.Should().Be("Fix hover");
        info.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture).Should().Be("2026-09-20");
        handler.Calls.Should().Be(1);

        (await lookup.TryGetAsync(commit)).Should().Be(info);
        handler.Calls.Should().Be(1);
    }

    [TestMethod]
    public async Task TryGetAsync_MissingOwner_ReturnsNull()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(handler);
        var lookup = new CommitInfoLookup(client);
        var commit = new CommitLink { RepoUrl = "https://example.test/repo", Hash = "abc1234" };

        var info = await lookup.TryGetAsync(commit);
        info.Should().BeNull();
        handler.Calls.Should().Be(0);
    }

    [TestMethod]
    public async Task TryGetAsync_FailedRequest_DoesNotRetry()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, "rate limited");
        using var client = new HttpClient(handler);
        var lookup = new CommitInfoLookup(client);
        var commit = new CommitLink
        {
            RepoUrl = "https://github.com/jjonescz/DotNetLab",
            Hash = "deadbeef",
        };

        (await lookup.TryGetAsync(commit)).Should().BeNull();
        (await lookup.TryGetAsync(commit)).Should().BeNull();
        handler.Calls.Should().Be(1);
    }

    [TestMethod]
    public async Task TryGetAsync_ConcurrentSameHash_PerformsOneRequest()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"commit":{"author":{"date":"2026-09-20T21:00:00Z"},"message":"One"}}""",
            delay: TimeSpan.FromMilliseconds(50));
        using var client = new HttpClient(handler);
        var lookup = new CommitInfoLookup(client);
        var commit = new CommitLink
        {
            RepoUrl = "https://github.com/jjonescz/DotNetLab",
            Hash = "concurrent",
        };

        var first = lookup.TryGetAsync(commit);
        var second = lookup.TryGetAsync(commit);
        var results = await Task.WhenAll(first, second);

        results[0].Should().NotBeNull();
        results[1].Should().Be(results[0]);
        handler.Calls.Should().Be(1);
    }

    private sealed class StubHandler(HttpStatusCode status, string body, TimeSpan delay = default) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            request.RequestUri!.Host.Should().Be("api.github.com");
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
