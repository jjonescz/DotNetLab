using AwesomeAssertions;
using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace DotNetLab;

public sealed class CompilerProxyTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("4.12.0-2.24409.2", "4.12.0-2.24409.2 (2158b591)")] // preview version is downloaded from an AzDo feed
    [InlineData("4.14.0", "4.14.0-3.25262.10 (8edf7bcd)")] // non-preview version is downloaded from nuget.org
    [InlineData("main", "-ci (<developer build>)")] // a branch can be downloaded
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
        // There are bunch of tests that do not assert much but they verify no type load exceptions happen.
        // Some older versions of Roslyn are not compatible though (until we actually download the corresponding older DLLs
        // for language services as well but we might never actually want to support that).

        var languageServices = await compiler.GetLanguageServicesAsync();
        await languageServices.OnDidChangeWorkspaceAsync([new("Input.cs", "Input.cs") { NewContent = "#error version" }]);

        var markers = await languageServices.GetDiagnosticsAsync("Input.cs");
        markers.Should().Contain(m => m.Message.Contains(expectedDiagnostic));

        var loadSemanticTokens = async () =>
        {
            var semanticTokensJson = await languageServices.ProvideSemanticTokensAsync("Input.cs", null, false, TestContext.Current.CancellationToken);
            semanticTokensJson.Should().NotBeNull();
        };

        if (version.StartsWith("4.12."))
        {
            await loadSemanticTokens.Should().ThrowAsync<TypeLoadException>();
        }
        else
        {
            await loadSemanticTokens();
        }

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
            """.ReplaceLineEndings(), diagnosticsText);
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
            @if (Param == 0)
            {
                <TestComponent Param="1" />
            }

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
        Assert.Contains("AddComponentParameter", cSharpText);

        var htmlText = await compiled.Files.Single().Value.GetRequiredOutput("html").GetTextAsync(outputFactory: null);
        output.WriteLine(htmlText);
        Assert.Equal("<div>42</div>", htmlText);
    }

    [Fact]
    public void DefaultCSharpDecompilerSettings()
    {
        var settings = Compiler.DefaultCSharpDecompilerSettings;

        // All properties except few should be false.
        var trueProperties = new List<string>();
        foreach (var property in settings.GetType().GetProperties())
        {
            // Skip known non-boolean properties.
            if (property.Name == nameof(settings.CSharpFormattingOptions))
            {
                continue;
            }

            Assert.Equal(typeof(bool), property.PropertyType);
            var value = (bool)property.GetValue(settings)!;
            if (value) trueProperties.Add(property.Name);
        }

        HashSet<string> expectedTrueProperties =
        [
            "AlwaysUseBraces",
            "UsingDeclarations",
            "UseDebugSymbols",
            "ShowXmlDocumentation",
            "DecompileMemberBodies",
            "AssumeArrayLengthFitsIntoInt32",
            "IntroduceIncrementAndDecrement",
            "MakeAssignmentExpressions",
            "ThrowOnAssemblyResolveErrors",
            "ApplyWindowsRuntimeProjections",
            "AutoLoadAssemblyReferences",
            "UseSdkStyleProjectFormat",
        ];

        trueProperties.RemoveAll(expectedTrueProperties.Remove);

        assertEmpty(trueProperties);
        assertEmpty(expectedTrueProperties);

        static void assertEmpty(ICollection<string> collection,
            [CallerArgumentExpression(nameof(collection))] string e = "collection")
        {
            if (collection.Count > 0)
            {
                var text = collection
                    .Select(p => $"""
                    "{p}"
                    """)
                    .JoinToString(", ");
                Assert.Fail($"Unexpected items in {e} ({collection.Count}): {text}");
            }
        }
    }

    [Theory, CombinatorialData]
    public async Task Directives_Configuration(bool debug)
    {
        var services = WorkerServices.CreateTest();

        var source = $"""
            #:property Configuration={(debug ? "Debug" : "Release")}
            System.Console.WriteLine("Hi");
            """;

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.cs", Text = source }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).EagerText;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Empty(diagnosticsText);

        var ilText = await compiled.GetRequiredGlobalOutput("il").GetTextAsync(null);
        output.WriteLine(ilText);
        Action<string, string> assert = debug ? Assert.Contains : Assert.DoesNotContain;
        assert("nop", ilText);
    }

    [Theory, CombinatorialData]
    public async Task Directives_LangVersion(bool old)
    {
        var services = WorkerServices.CreateTest();

        var source = $"""
            #:property LangVersion={(old ? "12" : "13")}
            class C<T> where T : allows ref struct;
            """;

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.cs", Text = source }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).EagerText;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);

        if (old)
        {
            Assert.Equal("""
                // /Input.cs(2,29): error CS9202: Feature 'allows ref struct constraint' is not available in C# 12.0. Please use language version 13.0 or greater.
                // class C<T> where T : allows ref struct;
                Diagnostic(ErrorCode.ERR_FeatureNotAvailableInVersion12, "ref struct").WithArguments("allows ref struct constraint", "13.0").WithLocation(2, 29)
                """.ReplaceLineEndings(), diagnosticsText);
        }
        else
        {
            Assert.Empty(diagnosticsText);
        }
    }

    [Theory, CombinatorialData]
    public async Task Directives_TargetFramework(bool fx)
    {
        var services = WorkerServices.CreateTest(output);

        var source = $$"""
            #:property TargetFramework={{(fx ? "net472" : "net9.0")}}
            class C
            {
                string M(string? x)
                {
                    System.Diagnostics.Debug.Assert(x != null);
                    return x;
                }
            }
            """;

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.cs", Text = source }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).EagerText;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);

        // .NET Framework does not have nullable-annotated `Debug.Assert` API.
        if (fx)
        {
            Assert.Equal("""
                // /Input.cs(7,16): warning CS8603: Possible null reference return.
                //         return x;
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "x").WithLocation(7, 16)
                """.ReplaceLineEndings(), diagnosticsText);
        }
        else
        {
            Assert.Empty(diagnosticsText);
        }
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
