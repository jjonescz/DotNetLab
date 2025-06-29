using AwesomeAssertions;
using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab;

public sealed class CompilerProxyTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("4.12.0-2.24409.2", "4.12.0-2.24409.2 (2158b591)")]
    [InlineData("main", "-ci (<developer build>)")]
    public async Task SpecifiedNuGetRoslynVersion(string version, string expectedDiagnostic)
    {
        var services = WorkerServices.CreateTest(new MockHttpMessageHandler(output));

        await services.GetRequiredService<CompilerDependencyProvider>()
            .UseAsync(CompilerKind.Roslyn, version, BuildConfiguration.Release);

        var compiler = services.GetRequiredService<CompilerProxy>();
        var compiled = await compiler.CompileAsync(new(new([new() { FileName = "Input.cs", Text = "#error version" }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).EagerText;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Contains(expectedDiagnostic, diagnosticsText);

        // Language services should also pick up the custom compiler version.
        var languageServices = await compiler.GetLanguageServicesAsync();
        await languageServices.OnDidChangeWorkspaceAsync([new("Input.cs", "Input.cs") { NewContent = "#error version" }]);
        languageServices.OnDidChangeModel("Input.cs");

        var markers = await languageServices.GetDiagnosticsAsync();
        markers.Should().Contain(m => m.Message.Contains(expectedDiagnostic));

        var codeActionsJson = await languageServices.ProvideCodeActionsAsync("Input.cs", null, TestContext.Current.CancellationToken);
        codeActionsJson.Should().NotBeNull();
    }

    [Theory]
    [InlineData("4.11.0-3.24352.2", "92051d4c")]
    [InlineData("4.10.0-1.24076.1", "e1c36b10")]
    [InlineData("5.0.0-1.25252.6", "b6ec1031")]
    public async Task SpecifiedNuGetRoslynVersion_WithConfiguration(string version, string commit)
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
        Assert.Equal($"""
            // /Input.cs(1,8): error CS1029: #error: 'version'
            // #error version
            Diagnostic(ErrorCode.ERR_ErrorDirective, "version").WithArguments("version").WithLocation(1, 8),
            // /Input.cs(1,8): error CS8304: Compiler version: '{version} ({commit})'. Language version: 10.0.
            // #error version
            Diagnostic(ErrorCode.ERR_CompilerAndLanguageVersion, "version").WithArguments("{version} ({commit})", "10.0").WithLocation(1, 8)
            """, diagnosticsText);
    }

    [Theory]
    [InlineData("9.0.0-preview.24413.5")]
    [InlineData("9.0.0-preview.25128.1")]
    [InlineData("10.0.0-preview.25252.1")]
    [InlineData("10.0.0-preview.25264.1")]
    [InlineData("10.0.0-preview.25311.107")]
    [InlineData("10.0.0-preview.25314.101")]
    [InlineData("main")] // test that we can download a branch
    public async Task SpecifiedNuGetRazorVersion(string version)
    {
        var services = WorkerServices.CreateTest(new MockHttpMessageHandler(output));

        await services.GetRequiredService<CompilerDependencyProvider>()
            .UseAsync(CompilerKind.Razor, version, BuildConfiguration.Release);

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "TestComponent.razor", Text = "test" }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).EagerText;
        Assert.NotNull(diagnosticsText);
        output.WriteLine("Diagnostics:");
        output.WriteLine(diagnosticsText);
        output.WriteLine(string.Empty);
        Assert.Empty(diagnosticsText);

        var cSharpText = await compiled.GetRequiredGlobalOutput("cs").GetTextAsync(outputFactory: null);
        output.WriteLine("C#:");
        output.WriteLine(cSharpText);
        output.WriteLine(string.Empty);
        Assert.Contains("class TestComponent", cSharpText);

        var syntaxText = await compiled.Files.First().Value.GetOutput("syntax")!.GetTextAsync(outputFactory: null);
        output.WriteLine("Syntax:");
        output.WriteLine(syntaxText);
        output.WriteLine(string.Empty);
        Assert.Contains("RazorDocument", syntaxText);

        var irText = await compiled.Files.First().Value.GetOutput("ir")!.GetTextAsync(outputFactory: null);
        output.WriteLine("IR:");
        output.WriteLine(irText);
        output.WriteLine(string.Empty);
        Assert.Contains("""component.1.0""", irText);
        Assert.Contains("TestComponent", irText);

        var htmlText = await compiled.Files.First().Value.GetOutput("html")!.GetTextAsync(outputFactory: null);
        output.WriteLine("HTML:");
        output.WriteLine(htmlText);
        output.WriteLine(string.Empty);
        Assert.Contains("test", htmlText);
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

internal sealed partial class MockHttpMessageHandler : HttpClientHandler
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
        if (request.RequestUri?.Host != "localhost")
        {
            testOutput.WriteLine($"Skipping mocking non-localhost request: {request.RequestUri}");
            return base.SendAsync(request, cancellationToken);
        }

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
