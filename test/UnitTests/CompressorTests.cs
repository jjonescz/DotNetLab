using DotNetLab.Lab;

namespace DotNetLab;

public sealed class CompressorTests
{
    [Fact]
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
        Assert.Equal((89, 114), (source.Length, compressed.Length));
        var uncompressed = Compressor.Uncompress(compressed);
        Assert.Equal(savedState.Inputs.Single(), uncompressed.Inputs.Single());
    }

    [Fact]
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
        Assert.Equal(expected.Inputs.Single(), actual.Inputs.Single());
    }
}
