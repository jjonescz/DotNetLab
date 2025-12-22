using AwesomeAssertions;
using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab;

public sealed class LabPageTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Markers_CSharp()
    {
        var fileName = "Input.cs";
        var source = """
            using System.Linq;
            string s = null;
            """;

        var services = WorkerServices.CreateTest(output, new MockHttpMessageHandler(output));
        var compiler = services.GetRequiredService<CompilerProxy>();
        var compiled = await compiler.CompileAsync(new(new([new() { FileName = fileName, Text = source }])));

        var markers = Page.GetMarkersForInputFile(compiled, fileName).Select(static m => m.ToDisplayString());
        markers.Should().Equal(
        [
            "(1,1): hint CS8019: Unnecessary using directive.",
            "(2,8): warning CS0219: The variable 's' is assigned but its value is never used",
            "(2,12): warning CS8600: Converting null literal or possible null value to non-nullable type.",
        ]);
    }

    [Theory]
    [InlineData(RazorToolchain.SourceGenerator)]
    [InlineData(RazorToolchain.InternalApi)]
    public async Task Markers_Razor(RazorToolchain toolchain)
    {
        var fileName = "Input.razor";
        var source = """
            @using System.Linq;
            @{ string s = null; }
            """;

        var services = WorkerServices.CreateTest(output, new MockHttpMessageHandler(output));
        var compiler = services.GetRequiredService<CompilerProxy>();
        var compiled = await compiler.CompileAsync(new(new([new() { FileName = fileName, Text = source }]))
        {
            RazorToolchain = toolchain,
        });

        var markers = Page.GetMarkersForInputFile(compiled, fileName).Select(static m => m.ToDisplayString());
        markers.Should().Equal(
        [
            "(1,2): hint CS8019: Unnecessary using directive.",
            "(2,11): warning CS0219: The variable 's' is assigned but its value is never used",
            "(2,15): warning CS8600: Converting null literal or possible null value to non-nullable type.",
        ]);
    }
}
