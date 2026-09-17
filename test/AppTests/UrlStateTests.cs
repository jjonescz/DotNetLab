using AwesomeAssertions;
using DotNetLab.Features.Sharing;
using DotNetLab.Lab;

namespace DotNetLab;

[TestClass]
public sealed class UrlStateTests
{
    [TestMethod]
    [DataRow(null, "")]
    [DataRow("", "")]
    [DataRow("csharp", "csharp")]
    [DataRow("  razor  ", "razor")]
    [DataRow("https://example/#cshtml", "cshtml")]
    [DataRow("https://example/#slug-with#extra", "slug-with#extra")]
    public void GetSlugFromClipboardText(string? text, string expected)
        => LabUrlSync.GetSlugFromClipboardText(text).Should().Be(expected);

    [TestMethod]
    [DataRow("csharp")]
    [DataRow("razor")]
    [DataRow("cshtml")]
    public void TryGetSavedStateFromSlug_WellKnown(string slug)
    {
        LabUrlSync.TryGetSavedStateFromSlug(slug, out var state).Should().BeTrue();
        state.Should().BeSameAs(WellKnownSlugs.ShorthandToState[slug]);
    }

    [TestMethod]
    public void TryGetSavedStateFromSlug_CompressedInitial()
    {
        var slug = Compressor.Compress(SavedState.Initial);
        LabUrlSync.TryGetSavedStateFromSlug(slug, out var state).Should().BeTrue();
        state!.Inputs.Should().BeEquivalentTo(SavedState.Initial.Inputs);
    }

    [TestMethod]
    public void TryGetSavedStateFromSlug_Garbage()
        => LabUrlSync.TryGetSavedStateFromSlug("%%%not-a-slug%%%", out _).Should().BeFalse();

    [TestMethod]
    [DataRow("abcdef12", true, "abcdef12")]
    [DataRow("https://gist.github.com/user/0123456789abcdef0123456789abcdef", true, "0123456789abcdef0123456789abcdef")]
    [DataRow("https://gist.githubusercontent.com/user/deadbeef/raw", true, "deadbeef")]
    [DataRow("https://github.com/user/repo", false, "")]
    [DataRow("", false, "")]
    public void TryParseGistId(string url, bool expected, string gistId)
    {
        LabLinks.TryParseGistId(url, out var parsed).Should().Be(expected);
        parsed.Should().Be(gistId);
    }
}
