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
    [InlineData("5.0.0-2.25472.1", "5.0.0-2.25472.1 (68435db2)")]
    [InlineData("main", "-ci (<developer build>)")] // a branch can be downloaded
    [InlineData("latest", "4.14.0-3.25262.10 (8edf7bcd)")] // `latest` works
    public async Task SpecifiedNuGetRoslynVersion(string version, string expectedDiagnostic)
    {
        var services = WorkerServices.CreateTest(new MockHttpMessageHandler(output));

        await services.GetRequiredService<CompilerDependencyProvider>()
            .UseAsync(CompilerKind.Roslyn, version, BuildConfiguration.Release);

        var compiler = services.GetRequiredService<CompilerProxy>();
        var compiled = await compiler.CompileAsync(new(new([new() { FileName = "Input.cs", Text = "#error version" }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Contains(expectedDiagnostic, diagnosticsText);

        // Language services should also pick up the custom compiler version.
        // There are bunch of tests that do not assert much but they verify no type load exceptions happen.
        // Some older versions of Roslyn are not compatible though (until we actually download the corresponding older DLLs
        // for language services as well but we might never actually want to support that).

        try
        {
            var languageServices = await compiler.GetLanguageServicesAsync();
            await languageServices.OnDidChangeWorkspaceAsync([new("Input.cs", "Input.cs") { NewContent = "#error version" }]);

            var markers = await languageServices.GetDiagnosticsAsync("Input.cs");
            markers.Should().Contain(m => m.Message.Contains(expectedDiagnostic));

            var semanticTokensJson = await languageServices.ProvideSemanticTokensAsync("Input.cs", null, false, TestContext.Current.CancellationToken);
            semanticTokensJson.Should().NotBeNull();

            var codeActionsJson = await languageServices.ProvideCodeActionsAsync("Input.cs", null, TestContext.Current.CancellationToken);
            codeActionsJson.Should().NotBeNull();
        }
        catch (Exception e) when (e is TypeLoadException or ReflectionTypeLoadException or MissingMethodException)
        {
            output.WriteLine(e.ToString());
            expectedDiagnostic.Should().StartWith("4.");
        }
    }

    [Theory]
    [InlineData("4.12.0-2.24409.2", "4.12.0-2.24409.2", "2158b59104a5fb7db33796657d4ab3231e312302")] // preview version is downloaded from an AzDo feed
    [InlineData("4.14.0", "4.14.0", "8edf7bcd4f1594c3d68a6a567469f41dbd33dd1b")] // non-preview version is downloaded from nuget.org
    public async Task SpecifiedNuGetRoslynVersion_Info(string version, string expectedVersion, string expectedCommit)
    {
        var services = WorkerServices.CreateTest(output, new MockHttpMessageHandler(output));

        var dependencyProvider = services.GetRequiredService<CompilerDependencyProvider>();

        await dependencyProvider.UseAsync(CompilerKind.Roslyn, version, BuildConfiguration.Release);

        var info = await dependencyProvider.GetLoadedInfoAsync(CompilerKind.Roslyn);

        info.Should().NotBeNull();
        info.Version.Should().Be(expectedVersion);
        info.Commit.Hash.Should().Be(expectedCommit);
        info.Commit.RepoUrl.Should().Be("https://github.com/dotnet/roslyn");
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

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Equal($"""
            // (1,8): error CS1029: #error: 'version'
            // #error version
            Diagnostic(ErrorCode.ERR_ErrorDirective, "version").WithArguments("version").WithLocation(1, 8),
            // (1,8): error CS8304: Compiler version: '{version} ({commit})'. Language version: 10.0.
            // #error version
            Diagnostic(ErrorCode.ERR_CompilerAndLanguageVersion, "version").WithArguments("{version} ({commit})", "10.0").WithLocation(1, 8)
            """.ReplaceLineEndings(), diagnosticsText);
    }

    /// <summary>
    /// <see href="https://github.com/jjonescz/DotNetLab/issues/102"/>
    /// </summary>
    [Theory]
    [InlineData("5.0.0-2.25451.107", "2db1f5ee")]
    public async Task SpecifiedNuGetRoslynVersion_WithNonEnglishCulture(string version, string commit)
    {
        var culture = new CultureInfo("cs");
        var previousDefaultThreadCulture = CultureInfo.DefaultThreadCurrentCulture;
        var previousDefaultThreadUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            var services = WorkerServices.CreateTest(output, new MockHttpMessageHandler(output));

            await services.GetRequiredService<CompilerDependencyProvider>()
                .UseAsync(CompilerKind.Roslyn, version, BuildConfiguration.Release);

            var compiled = await services.GetRequiredService<CompilerProxy>()
                .CompileAsync(new(new([new() { FileName = "Input.cs", Text = "#error version" }])));

            var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
            Assert.NotNull(diagnosticsText);
            output.WriteLine(diagnosticsText);
            Assert.Equal($"""
                // (1,8): error CS1029: #error: 'version'
                // #error version
                Diagnostic(ErrorCode.ERR_ErrorDirective, "version").WithArguments("version").WithLocation(1, 8),
                // (1,8): error CS8304: Compiler version: '{version} ({commit})'. Language version: preview.
                // #error version
                Diagnostic(ErrorCode.ERR_CompilerAndLanguageVersion, "version").WithArguments("{version} ({commit})", "preview").WithLocation(1, 8)
                """.ReplaceLineEndings(), diagnosticsText);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = previousDefaultThreadCulture;
            CultureInfo.DefaultThreadCurrentUICulture = previousDefaultThreadUiCulture;
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Theory]
    [InlineData("9.0.0-preview.24413.5")]
    [InlineData("9.0.0-preview.25128.1")]
    [InlineData("10.0.0-preview.25252.1")]
    [InlineData("10.0.0-preview.25264.1")]
    [InlineData("10.0.0-preview.25311.107")]
    [InlineData("10.0.0-preview.25314.101")]
    [InlineData("10.0.0-preview.25429.2")]
    [InlineData("main")] // test that we can download a branch
    [InlineData("latest")] // `latest` works
    public async Task SpecifiedNuGetRazorVersion(string version)
    {
        var services = WorkerServices.CreateTest(new MockHttpMessageHandler(output));

        await services.GetRequiredService<CompilerDependencyProvider>()
            .UseAsync(CompilerKind.Razor, version, BuildConfiguration.Release);

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "TestComponent.razor", Text = "test" }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.NotNull(diagnosticsText);
        output.WriteLine("Diagnostics:");
        output.WriteLine(diagnosticsText);
        output.WriteLine(string.Empty);
        Assert.Empty(diagnosticsText);

        var cSharpText = (await compiled.GetRequiredGlobalOutput("cs").LoadAsync(outputFactory: null)).Text;
        output.WriteLine("C#:");
        output.WriteLine(cSharpText);
        output.WriteLine(string.Empty);
        Assert.Contains("class TestComponent", cSharpText);

        var syntaxText = (await compiled.Files.First().Value.GetOutput("syntax")!.LoadAsync(outputFactory: null)).Text;
        output.WriteLine("Syntax:");
        output.WriteLine(syntaxText);
        output.WriteLine(string.Empty);
        Assert.Contains("RazorDocument", syntaxText);

        var irText = (await compiled.Files.First().Value.GetOutput("ir")!.LoadAsync(outputFactory: null)).Text;
        output.WriteLine("IR:");
        output.WriteLine(irText);
        output.WriteLine(string.Empty);
        Assert.Contains("""component.1.0""", irText);
        Assert.Contains("TestComponent", irText);

        var htmlText = (await compiled.Files.First().Value.GetOutput("html")!.LoadAsync(outputFactory: null)).Text;
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

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Empty(diagnosticsText);

        var cSharpText = (await compiled.GetRequiredGlobalOutput("cs").LoadAsync(outputFactory: null)).Text;
        output.WriteLine(cSharpText);
        Assert.Contains("class TestComponent", cSharpText);
        Assert.Contains("AddComponentParameter", cSharpText);

        var htmlText = (await compiled.Files.Single().Value.GetRequiredOutput("html").LoadAsync(outputFactory: null)).Text;
        output.WriteLine(htmlText);
        Assert.Equal("<div>42</div>", htmlText);
    }

    [Theory, CombinatorialData]
    public async Task AsyncMain(bool args)
    {
        var services = WorkerServices.CreateTest(output);

        string code = $$"""
            using System;
            using System.Threading.Tasks;
            class Program
            {
                async static Task<int> Main({{(args ? "string[] args" : "")}})
                {
                    Console.Write("Hello.");
                    await Task.Delay(1);
                    return 42;
                }
            }
            """;

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.cs", Text = code }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Empty(diagnosticsText);

        var runText = (await compiled.GetRequiredGlobalOutput("run").LoadAsync(outputFactory: null)).Text;
        output.WriteLine(runText);
        Assert.Equal("""
            Exit code: 42
            Stdout:
            Hello.
            Stderr:

            """.ReplaceLineEndings("\n"), runText);
    }

    [Fact]
    public async Task DecompileExtension()
    {
        var services = WorkerServices.CreateTest(output);

        string code = """
            static class E
            {
                public static int M(this int x) => x;
            }
            """;

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.cs", Text = code }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Empty(diagnosticsText);

        var cSharpText = (await compiled.GetRequiredGlobalOutput("cs").LoadAsync(outputFactory: null)).Text;
        output.WriteLine(cSharpText);
        Assert.Contains("[Extension]", cSharpText);
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
            "LoadInMemory",
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

    [Fact]
    public async Task ILDecompilationUsesSpacesForIndentation()
    {
        var services = WorkerServices.CreateTest(new MockHttpMessageHandler(output));
        var compiler = services.GetRequiredService<CompilerProxy>();

        var code = """
public class C
{
    public static void Main()
    {
        System.Console.WriteLine("Hello, World!");
    }
}
""";

        var compiled = await compiler.CompileAsync(new(new([new() { FileName = "Program.cs", Text = code }])));

        string ilText = (await compiled.GetRequiredGlobalOutput("il").LoadAsync(outputFactory: null)).Text;
        Assert.DoesNotContain("\t", ilText);

        // Verify that the IL contains expected content
        Assert.Contains(".method", ilText);
        Assert.Contains("Main", ilText);

        // Count leading spaces on each indented line and verify they are multiples of 4
        var indentedLines = ilText.Split('\n').Where(line => line.StartsWith(" ") && line.Length > 1).ToArray();
        bool found4Spaces = false;
        bool found8Spaces = false;
        for (int i = 0; i < indentedLines.Length; i++)
        {
            int leadingSpaces = countLeadingSpaces(indentedLines[i]);
            if (leadingSpaces == 4)
            {
                found4Spaces = true;
            }
            else if (leadingSpaces == 8)
            {
                found8Spaces = true;
            }

            Assert.True(leadingSpaces % 4 == 0, "All lines should be indented by a multiple of 4 spaces");
        }

        Assert.True(found4Spaces);
        Assert.True(found8Spaces);

        return;

        static int countLeadingSpaces(string line)
        {
            int currentPos = 0;
            while (currentPos < line.Length && line[currentPos] == ' ')
            {
                currentPos++;
            }

            return currentPos;
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

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Empty(diagnosticsText);

        var ilText = (await compiled.GetRequiredGlobalOutput("il").LoadAsync(null)).Text;
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

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);

        if (old)
        {
            Assert.Equal("""
                // (2,29): error CS9202: Feature 'allows ref struct constraint' is not available in C# 12.0. Please use language version 13.0 or greater.
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

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);

        // .NET Framework does not have nullable-annotated `Debug.Assert` API.
        if (fx)
        {
            Assert.Equal("""
                // (7,16): warning CS8603: Possible null reference return.
                //         return x;
                Diagnostic(ErrorCode.WRN_NullReferenceReturn, "x").WithLocation(7, 16)
                """.ReplaceLineEndings(), diagnosticsText);
        }
        else
        {
            Assert.Empty(diagnosticsText);
        }
    }

    [Fact]
    public async Task Directives_Package()
    {
        var services = WorkerServices.CreateTest(output);

        var source = """
            #:package Humanizer
            using Humanizer;
            using System;
            Console.Write(DateTimeOffset.Now.Humanize());
            """;

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.cs", Text = source }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Empty(diagnosticsText);

        var runText = (await compiled.GetRequiredGlobalOutput("run").LoadAsync(null)).Text;
        output.WriteLine(runText);
        Assert.Equal("""
            Exit code: 0
            Stdout:
            now
            Stderr:
            """.ReplaceLineEndings("\n"), runText.Trim());
    }

    [Fact]
    public async Task Directives_Package_Unification()
    {
        var services = WorkerServices.CreateTest(output);

        var source = """
            #:package System.Reactive.Providers@3.1.1
            #:package System.Reactive.PlatformServices@3.0.0
            using System;
            using System.Reactive.Linq;
            using System.Reactive.PlatformServices;
            _ = Qbservable.Provider; // System.Reactive.Providers needed
            _ = typeof(CurrentPlatformEnlightenmentProvider); // System.Reactive.PlatformServices needed
            Console.Write(typeof(Observable).Assembly.GetName().Version); // System.Reactive.Linq, version 3.0.3000.0 = 3.1.1
            """;

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.cs", Text = source }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Empty(diagnosticsText);

        var runText = (await compiled.GetRequiredGlobalOutput("run").LoadAsync(null)).Text;
        output.WriteLine(runText);
        Assert.Equal("""
            Exit code: 0
            Stdout:
            3.0.3000.0
            Stderr:
            """.ReplaceLineEndings("\n"), runText.Trim());
    }

    /// <summary>
    /// In this example, the referenced package references ASP.NET Core v9 but we have v10+.
    /// We should not pass both to the compiler to avoid compiler errors due to duplicate references.
    /// </summary>
    [Fact]
    public async Task Directives_Package_DuplicateRefs()
    {
        var services = WorkerServices.CreateTest(output);

        var csSource = """
            #:package Microsoft.FluentUI.AspNetCore.Components@4.12.1
            """;

        var razorSource = """
            @using Microsoft.FluentUI.AspNetCore.Components
            <FluentButton />
            """;

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new(
            [
                new() { FileName = "Input.cs", Text = csSource },
                new() { FileName = "Input.razor", Text = razorSource },
            ])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Empty(diagnosticsText);

        var csText = (await compiled.GetRequiredGlobalOutput("cs").LoadAsync(null)).Text;
        output.WriteLine(csText);
        Assert.Contains("__builder.OpenComponent<FluentButton>", csText);
    }

    [Fact]
    public async Task Directives_Package_Roslyn()
    {
        var services = WorkerServices.CreateTest(output);

        var source = """
            #:package Microsoft.CodeAnalysis@*-*
            #:package Basic.Reference.Assemblies.Net100@*-*

            using System;
            using Microsoft.CodeAnalysis;
            using Microsoft.CodeAnalysis.CSharp;
            using Basic.Reference.Assemblies;

            var compilation = CSharpCompilation.Create(
                "Test",
                [CSharpSyntaxTree.ParseText("class C {")],
                Net100.References.All,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Console.Write(string.Join("\n", compilation.GetDiagnostics()));
            """;

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.cs", Text = source }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Empty(diagnosticsText);

        var runText = (await compiled.GetRequiredGlobalOutput("run").LoadAsync(null)).Text;
        output.WriteLine(runText);
        Assert.Equal("""
            Exit code: 0
            Stdout:
            (1,10): error CS1513: } expected
            Stderr:
            """.ReplaceLineEndings("\n"), runText.Trim());
    }

    /// <summary>
    /// When one package does not exist, the other should still be downloaded.
    /// </summary>
    [Fact]
    public async Task Directives_Package_OneDoesNotExist()
    {
        var services = WorkerServices.CreateTest(output);

        var source = """
            #:package Humanizer.Core
            #:package Microsoft.CodeAnalysis@1000
            using Humanizer;
            using System;
            Console.Write(DateTimeOffset.Now.Humanize());
            """;

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.cs", Text = source }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.NotNull(diagnosticsText);
        output.WriteLine(diagnosticsText);
        Assert.Equal("""
            // (2,1): warning LAB: Cannot find a version for package 'Microsoft.CodeAnalysis' in range '[1000.0.0, )'.
            // #:package Microsoft.CodeAnalysis@1000
            Diagnostic("LAB", "#:package Microsoft.CodeAnalysis@1000").WithLocation(2, 1)
            """.ReplaceLineEndings(), diagnosticsText);

        var runText = (await compiled.GetRequiredGlobalOutput("run").LoadAsync(null)).Text;
        output.WriteLine(runText);
        Assert.Equal("""
            Exit code: 0
            Stdout:
            now
            Stderr:
            """.ReplaceLineEndings("\n"), runText.Trim());
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
