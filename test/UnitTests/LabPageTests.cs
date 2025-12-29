using AwesomeAssertions;
using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab;

[TestClass]
public sealed class LabPageTests
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    public async Task Markers_CSharp()
    {
        var fileName = "Input.cs";
        var source = """
            using System.Linq;
            string s = null;
            """;

        var services = WorkerServices.CreateTest(TestContext, new MockHttpMessageHandler(TestContext));
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

    [TestMethod]
    [DataRow(RazorToolchain.SourceGenerator)]
    [DataRow(RazorToolchain.InternalApi)]
    public async Task Markers_Razor(RazorToolchain toolchain)
    {
        var fileName = "Input.razor";
        var source = """
            @using System.Linq;
            @{ string s = null; }
            """;

        var services = WorkerServices.CreateTest(TestContext, new MockHttpMessageHandler(TestContext));
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

    [TestMethod]
    public async Task Markers_Configuration()
    {
        var services = WorkerServices.CreateTest(TestContext, new MockHttpMessageHandler(TestContext));
        var compiler = services.GetRequiredService<CompilerProxy>();
        var compiled = await compiler.CompileAsync(new(new(
        [
            new() { FileName = "A.cs", Text = "using System.Linq;" },
            new() { FileName = "Z.cs", Text = "string s = null;" },
        ]))
        {
            Configuration = "void F() { }",
        });

        Page.GetMarkersForInputFile(compiled, "A.cs")
            .Select(static m => m.ToDisplayString())
            .Should().Equal(
            [
                "(1,1): hint CS8019: Unnecessary using directive.",
            ]);

        Page.GetMarkersForInputFile(compiled, "Z.cs")
            .Select(static m => m.ToDisplayString())
            .Should().Equal(
            [
                "(1,8): warning CS0219: The variable 's' is assigned but its value is never used",
                "(1,12): warning CS8600: Converting null literal or possible null value to non-nullable type.",
            ]);

        Page.GetMarkersForConfiguration(compiled)
            .Select(static m => m.ToDisplayString())
            .Should().Equal(
            [
                "(1,6): warning CS8321: The local function 'F' is declared but never used",
            ]);
    }
}
