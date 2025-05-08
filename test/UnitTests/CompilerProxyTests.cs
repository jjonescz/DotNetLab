using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab;

public sealed class CompilerProxyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SpecifiedNuGetRoslynVersion()
    {
        var services = WorkerServices.CreateTest(new MockHttpMessageHandler(output));

        var version = "4.12.0-2.24409.2";
        var commit = "2158b591";

        await services.GetRequiredService<CompilerDependencyProvider>()
            .UseAsync(CompilerKind.Roslyn, version, BuildConfiguration.Release);

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.cs", Text = "#error version" }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).EagerText;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Contains($"{version} ({commit})", diagnosticsText);
    }

    [Theory]
    [InlineData("4.11.0-3.24352.2", "92051d4c")]
    [InlineData("4.10.0-1.24076.1", "e1c36b10")]
    public async Task SpecifiedNuGetRoslynVersion_OlderWithConfiguration(string version, string commit)
    {
        var services = WorkerServices.CreateTest(new MockHttpMessageHandler(output));

        await services.GetRequiredService<CompilerDependencyProvider>()
            .UseAsync(CompilerKind.Roslyn, version, BuildConfiguration.Release);

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.cs", Text = "#error version" }]))
            {
                Configuration = """
                    Config.CSharpParseOptions(options => options
                        .WithLanguageVersion(LanguageVersion.CSharp10));
                    """,
            });

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).EagerText;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Contains($"{version} ({commit})", diagnosticsText);
        Assert.Contains("Language version: 10.0", diagnosticsText);
    }

    [Theory]
    [InlineData("9.0.0-preview.24413.5")]
    [InlineData("9.0.0-preview.25128.1")]
    [InlineData("10.0.0-preview.25252.1")]
    public async Task SpecifiedNuGetRazorVersion(string version)
    {
        var services = WorkerServices.CreateTest(new MockHttpMessageHandler(output));

        await services.GetRequiredService<CompilerDependencyProvider>()
            .UseAsync(CompilerKind.Razor, version, BuildConfiguration.Release);

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "TestComponent.razor", Text = "test" }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).EagerText;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Empty(diagnosticsText);

        var cSharpText = await compiled.GetRequiredGlobalOutput("cs").GetTextAsync(outputFactory: null);
        output.WriteLine(cSharpText);
        Assert.Contains("class TestComponent", cSharpText);
    }

    [Theory]
    [InlineData(RazorToolchain.SourceGenerator, RazorStrategy.Runtime)]
    [InlineData(RazorToolchain.InternalApi, RazorStrategy.Runtime)]
    [InlineData(RazorToolchain.InternalApi, RazorStrategy.DesignTime)]
    public async Task SpecifiedRazorOptions(RazorToolchain toolchain, RazorStrategy strategy)
    {
        var services = WorkerServices.CreateTest(new MockHttpMessageHandler(output));

        string code = """
            <div>@Param</div>

            @code {
                [Parameter] public int Param { get; set; } = 42;
            }
            """;

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "TestComponent.razor", Text = code }]))
            {
                RazorToolchain = toolchain,
                RazorStrategy = strategy,
            });

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).EagerText;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Empty(diagnosticsText);

        var cSharpText = await compiled.GetRequiredGlobalOutput("cs").GetTextAsync(outputFactory: null);
        output.WriteLine(cSharpText);
        Assert.Contains("class TestComponent", cSharpText);

        var htmlText = await compiled.Files.Single().Value.GetRequiredOutput("html").GetTextAsync(outputFactory: null);
        output.WriteLine(htmlText);
        Assert.Equal("<div>42</div>", htmlText);
    }
}

internal sealed partial class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly ITestOutputHelper testOutput;
    private readonly string directory;

    public MockHttpMessageHandler(ITestOutputHelper testOutput)
    {
        this.testOutput = testOutput;
        directory = Path.GetDirectoryName(GetType().Assembly.Location)!;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        testOutput.WriteLine($"Mocking request: {request.RequestUri}");

        if (UrlRegex.Match(request.RequestUri?.ToString() ?? "") is
            {
                Success: true,
                Groups: [_, { ValueSpan: var fileName }],
            })
        {
            if (fileName.EndsWith(".wasm", StringComparison.Ordinal))
            {
                var assemblyName = fileName[..^5];
                var assemblyPath = Path.Join(directory, assemblyName) + ".dll";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(File.OpenRead(assemblyPath)),
                });
            }
        }

        throw new NotImplementedException(request.RequestUri?.ToString());
    }

    [GeneratedRegex("""^https?://localhost/_framework/(.*)$""")]
    private static partial Regex UrlRegex { get; }
}
