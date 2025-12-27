using DotNetLab.Lab;

namespace DotNetLab;

[TestClass]
public sealed class CompressorTests
{
    [TestMethod]
    public void RazorInitialCode()
    {
        var source = """
            <TestComponent Param="1" />
            
            @code {
                [Parameter] public int Param { get; set; }
            }
            """.ReplaceLineEndings("\r\n");
        var savedState = new SavedState() { Inputs = [new() { FileName = "", Text = source }] };
        var compressed = Compressor.Compress(savedState);
        Assert.AreEqual((89, 114), (source.Length, compressed.Length));
        var uncompressed = Compressor.Uncompress(compressed);
        Assert.AreEqual(savedState.Inputs.Single(), uncompressed.Inputs.Single());
    }

    [TestMethod]
    public void BackwardsCompatibility()
    {
        // Do not change this string, we need to ensure it's always successfully parsed
        // to ensure old URLs can be opened in new versions of the app.
        var state = """
            48rhEg5JLS5xzs8tyM9LzSvRK0qsyi8SCrVBEVUISCxKzLVVMlRS0Lfj4nJIzk9JVajmUgCCaLBUaklqUaxCQWlSTmayQiZMg0K1QnpqibVCMYio5aoFAA
            """;
        var actual = Compressor.Uncompress(state);
        var expected = new SavedState()
        {
            Inputs =
            [
                new()
                {
                    FileName = "TestComponent.razor",
                    Text = """
                        <TestComponent Param="1" />

                        @code {
                            [Parameter] public int Param { get; set; }
                        }
                        """.ReplaceLineEndings("\n"),
                },
            ],
        };
        Assert.AreEqual(expected.Inputs.Single(), actual.Inputs.Single());
    }
}
