using AwesomeAssertions;
using BlazorMonaco;
using DotNetLab.Lab;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting.Combinatorial;
using System.Text.Json;

namespace DotNetLab;

[TestClass]
public sealed class LanguageServiceTests
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    [DataRow("void func() { }", "CS8321", "Remove unused function")]
    [DataRow("Console.WriteLine();", "CS0103", "using System;")]
    [DataRow("{ return 1; return 2; }", "CS0162", "Remove unreachable code")]
    public async Task CodeActions(string code, string expectedErrorCode, string expectedCodeActionTitle)
    {
        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        await languageServices.OnDidChangeWorkspaceAsync([new("test.cs", "test.cs") { NewContent = code }]);

        var markers = await languageServices.GetDiagnosticsAsync("test.cs");
        markers.Should().NotBeEmpty();
        TestContext.WriteLine($"Diagnostics:\n{markers.Select(m => $"{m.Message} ({m.Code})").JoinToString("\n")}");

        var codeActionsJson = await languageServices.ProvideCodeActionsAsync("test.cs", null, TestContext.CancellationToken);
        var codeActions = JsonSerializer.Deserialize(codeActionsJson!, BlazorMonacoJsonContext.Default.ImmutableArrayMonacoCodeAction);

        codeActions.Should().NotBeEmpty();
        TestContext.WriteLine($"Code actions:\n{codeActions.Select(c => $"{c.Title}: {c.Edit?.Edits.Select(e => e.TextEdit.Text).JoinToString(", ") ?? "null"}").JoinToString("\n")}");

        markers.Select(m => m.Code.ToString()).Should().ContainMatch($"*{expectedErrorCode}*");
        codeActions.Select(c => c.Title).Should().Contain(expectedCodeActionTitle);
    }

    /// <summary>
    /// Compiler and IDE diagnostics should be merged without duplicates and without dropping any.
    /// </summary>
    [TestMethod]
    public async Task Diagnostics()
    {
        static CompilationInput getInput(string str) => new(new(
        [
            new()
            {
                FileName = "test.cs",
                Text = $$"""
                    #:property ImplicitUsings=xyz
                    using System.Linq;
                    class Program
                    {
                        static void Main()
                        {
                            Console.WriteLine("{{str}}");
                            _ = new int[0][].Sum(static a => a.Sum(static b => b));
                        }
                    }
                    """,
            },
            new() { FileName = "Options.cs", Text = "#:property RunAnalyzers=true" },
        ]));

        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();

        var input = getInput("v1");
        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        await compiler.CompileAsync(input);
        await languageServices.OnCompilationFinishedAsync();

        await VerifyDiagnosticsAsync(languageServices, "test.cs",
        [
            // Compiler only
            "(1,1): warning LAB: Invalid property value 'xyz'. Expected one of 'enable', 'disable'.",
            // IDE only, info downgraded to hint
            "(3,7): hint IDE0040: Accessibility modifiers required",
            "(5,17): hint IDE0040: Accessibility modifiers required",
            // IDE + Compiler, deduplicated
            "(7,9): error CS0103: The name 'Console' does not exist in the current context",
            // IDE + Compiler, info not downgraded
            "(8,57): info CS9236: Compiling requires binding the lambda expression at least 100 times. Consider declaring the lambda expression with explicit parameter types, or if the containing method call is generic, consider using explicit type arguments.",
        ]);

        await VerifyDiagnosticsAsync(languageServices, "other.cs", []);

        // After some change, compiler diagnostics are dropped.
        input = getInput("v2");
        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        await VerifyDiagnosticsAsync(languageServices, "test.cs",
        [
            "(3,7): hint IDE0040: Accessibility modifiers required",
            "(5,17): hint IDE0040: Accessibility modifiers required",
            "(7,9): error CS0103: The name 'Console' does not exist in the current context",
            // TODO: We should consider improving the info->hint downgrading logic so this is reported as info.
            "(8,57): hint CS9236: Compiling requires binding the lambda expression at least 100 times. Consider declaring the lambda expression with explicit parameter types, or if the containing method call is generic, consider using explicit type arguments.",
        ]);
    }

    private const string uriGuidOverride = "test";

    private static ImmutableArray<ModelInfo> ToModelInfos(CompilationInput compilationInput)
    {
        return compilationInput.Inputs.Value
            .Select(static input => createModelInfo(fileName: input.FileName, text: input.Text))
            .Concat(compilationInput.Configuration == null
                ? []
                : [
                    createModelInfo(
                        fileName: CompiledAssembly.ConfigurationFileName,
                        text: compilationInput.Configuration,
                        isConfiguration: true),
                ])
            .ToImmutableArray();

        static ModelInfo createModelInfo(string fileName, string text, bool isConfiguration = false)
        {
            var uri = CompiledAssembly.GetInputModelUri(fileName, guidOverride: uriGuidOverride);
            return new(Uri: uri, FileName: fileName)
            {
                NewContent = text,
                IsConfiguration = isConfiguration,
            };
        }
    }

    private async Task VerifyDiagnosticsAsync(ILanguageServices languageServices, string file, string[] expectedDiagnostics, string? modelUri = null)
    {
        var uri = modelUri ?? CompiledAssembly.GetInputModelUri(file, guidOverride: uriGuidOverride);
        var markers = await languageServices.GetDiagnosticsAsync(uri);
        var actualDiagnostics = markers
            .OrderBy(m => (m.StartLineNumber, m.StartColumn, m.Severity, m.GetCode(), m.Message))
            .Select(static m => m.ToDisplayString())
            .ToArray();

        TestContext.WriteLine($"Actual diagnostics for '{file}' ({actualDiagnostics.Length}):");
        TestContext.WriteLine(actualDiagnostics.JoinToString("\n"));

        actualDiagnostics.Should().Equal(expectedDiagnostics);

        if (languageServices.CompilerCache is { } compilerCache)
        {
            var compilerDiagnosticData = compilerCache.GetDiagnosticsForFile(file,
                configuration: file == CompiledAssembly.ConfigurationFileName);
            var compilerDiagnosticDataStrings = compilerDiagnosticData
                .Where(static d => d.Data.Severity != DiagnosticDataSeverity.Hint)
                .Select(static d => d.ToMarkerData().ToDisplayString())
                .ToArray();

            compilerDiagnosticDataStrings.Should().BeSubsetOf(expectedDiagnostics);
        }
    }

    [TestMethod]
    [DataRow(RazorToolchain.SourceGenerator)]
    [DataRow(RazorToolchain.InternalApi)]
    public async Task Diagnostics_Razor(RazorToolchain toolchain)
    {
        var input = new CompilationInput(new(
        [
            new()
            {
                FileName = "Input.razor",
                Text = """
                    @using System.Linq;
                    @{ string s = null; }
                    """,
            },
            new() { FileName = "other.cs", Text = "using System.Linq;" },
            new() { FileName = "Options.cs", Text = "#:property RunAnalyzers=true" },
        ]))
        {
            RazorToolchain = toolchain,
        };

        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        await compiler.CompileAsync(input);
        await languageServices.OnCompilationFinishedAsync();

        await VerifyDiagnosticsAsync(languageServices, "Input.razor",
        [
            "(2,11): warning CS0219: The variable 's' is assigned but its value is never used",
            "(2,15): warning CS8600: Converting null literal or possible null value to non-nullable type.",
        ]);

        await VerifyDiagnosticsAsync(languageServices, "other.cs",
        [
            "(1,1): hint IDE0005: Using directive is unnecessary.",
            "(1,1): hint RemoveUnnecessaryImportsFixable: ",
        ]);
    }

    [TestMethod, CombinatorialData]
    public async Task Diagnostics_Configuration(bool loadAssembliesFromDisk)
    {
        var input = new CompilationInput(new(
        [
            new() { FileName = "A.cs", Text = "using System.Linq;" },
            new() { FileName = "Z.cs", Text = "string s = null;" },
            new() { FileName = "Options.cs", Text = "#:property RunAnalyzers=true" },
        ]))
        {
            Configuration = "void F() { }",
        };

        using var httpMessageHandler = new MockHttpMessageHandler(TestContext);
        var services = WorkerServices.CreateTest(TestContext, httpMessageHandler, services =>
            services.Configure<CompilerProxyOptions>(options => options.LoadAssembliesFromDisk = loadAssembliesFromDisk));
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        await compiler.CompileAsync(input);
        await languageServices.OnCompilationFinishedAsync();

        await VerifyDiagnosticsAsync(languageServices, "A.cs",
        [
            "(1,1): hint IDE0005: Using directive is unnecessary.",
            "(1,1): hint RemoveUnnecessaryImportsFixable: ",
        ]);

        await VerifyDiagnosticsAsync(languageServices, "Z.cs",
        [
            "(1,8): hint IDE0059: Unnecessary assignment of a value to 's'",
            "(1,8): warning CS0219: The variable 's' is assigned but its value is never used",
            "(1,12): warning CS8600: Converting null literal or possible null value to non-nullable type.",
        ]);

        await VerifyDiagnosticsAsync(languageServices, CompiledAssembly.ConfigurationFileName,
        [
            "(1,6): hint IDE0062: Local function can be made static",
            "(1,6): warning CS8321: The local function 'F' is declared but never used",
        ]);
    }

    [TestMethod]
    public async Task Diagnostics_Suppress()
    {
        var input = new CompilationInput(new(
        [
            new()
            {
                FileName = "test.cs",
                Text = """
                    public class C
                    {
                        public extern void M1();
                    #pragma warning disable CS0626 // extern without attributes
                        public extern void M2();
                    }
                    """,
            },
        ]));

        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        await compiler.CompileAsync(input);
        await languageServices.OnCompilationFinishedAsync();

        await VerifyDiagnosticsAsync(languageServices, "test.cs",
        [
            "(3,24): warning CS0626: Method, operator, or accessor 'C.M1()' is marked external and has no attributes on it. Consider adding a DllImport attribute to specify the external implementation.",
        ]);
    }

    /// <summary>
    /// Even with <c>RunAnalyzers=false</c>, we want to run roslyn analyzers and fade unused code.
    /// </summary>
    [TestMethod, CombinatorialData]
    public async Task Diagnostics_BuiltInAnalyzerFading(bool runAnalyzers)
    {
        var code = $$"""
            #:property RunAnalyzers={{runAnalyzers}}
            using System.Linq;
            class C
            {
                private void Unused() { }
                public void M() { int unused; }
            }
            """;
        var input = new CompilationInput(new([new() { FileName = "test.cs", Text = code }]));
        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        var uri = CompiledAssembly.GetInputModelUri("test.cs", guidOverride: uriGuidOverride);

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        if (!runAnalyzers)
        {
            var initialMarkers = await languageServices.GetDiagnosticsAsync(uri);
            initialMarkers.Should().Contain(m => m.GetCode() == "IDE0005");
            initialMarkers.Should().Contain(m => m.GetCode() == "IDE0051");
        }

        await compiler.CompileAsync(input);
        await languageServices.OnCompilationFinishedAsync();
        var markers = await languageServices.GetDiagnosticsAsync(uri);
        foreach (var diagnosticId in new[] { "IDE0005", "IDE0051" })
        {
            var marker = markers.Should().ContainSingle(m => m.GetCode() == diagnosticId).Which;
            marker.Severity.Should().Be(MarkerSeverity.Hint);
            marker.Tags.Should().Contain(MarkerTag.Unnecessary);
        }
        markers.Should().Contain(m => m.GetCode() == "CS0168" && m.Severity == MarkerSeverity.Warning);
        markers.Any(m => m.GetCode() == "IDE0040").Should().Be(runAnalyzers);
        if (!runAnalyzers)
        {
            markers.Where(m => m.GetCode()?.StartsWith("IDE", StringComparison.Ordinal) == true)
                .Should().OnlyContain(m => m.Severity == MarkerSeverity.Hint &&
                    m.Tags != null && m.Tags.Contains(MarkerTag.Unnecessary));
        }

        var updatedInput = new CompilationInput(new(
        [
            new()
            {
                FileName = "test.cs",
                Text = code.Replace("int unused;", "int unused; Unused(); _ = Enumerable.Empty<int>();"),
            },
        ]));
        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(updatedInput));
        markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().NotContain(m => m.GetCode() == "IDE0005" || m.GetCode() == "IDE0051");
    }

    [TestMethod, CombinatorialData]
    public async Task Diagnostics_Hidden(bool runAnalyzers)
    {
        var input = new CompilationInput(new(
        [
            new()
            {
                FileName = "test.cs",
                Text = """
                    using System;
                    """,
            },
            new() { FileName = "Options.cs", Text = $"#:property RunAnalyzers={runAnalyzers}" },
        ]));

        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        await compiler.CompileAsync(input);
        await languageServices.OnCompilationFinishedAsync();

        string[] analyzerDiagnostics =
        [
            "(1,1): hint IDE0005: Using directive is unnecessary.",
            .. runAnalyzers ? ["(1,1): hint RemoveUnnecessaryImportsFixable: "] : Array.Empty<string>(),
        ];
        await VerifyDiagnosticsAsync(languageServices, "test.cs", analyzerDiagnostics);

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        await VerifyDiagnosticsAsync(languageServices, "test.cs", analyzerDiagnostics);

        // When we explicitly include hidden diagnostics, they should be upgraded to info so they show up in the editor.
        input = input with { Preferences = input.Preferences with { IncludeHiddenDiagnostics = true } };

        await compiler.CompileAsync(input);
        await languageServices.OnCompilationFinishedAsync();

        await VerifyDiagnosticsAsync(languageServices, "test.cs",
        [
            .. analyzerDiagnostics,
            "(1,1): info CS8019: Unnecessary using directive.",
        ]);
    }

    [TestMethod]
    public async Task Diagnostics_LinePragma()
    {
        var input = new CompilationInput(new(
        [
            new()
            {
                FileName = "test.cs",
                Text = """
                    object o = new();
                    #line 100
                    string s = o;
                    #line 200 "another.cs"
                    int i = o;
                    """,
            },
            new() { FileName = "Options.cs", Text = "#:property RunAnalyzers=true" },
        ]));

        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        var compiled = await compiler.CompileAsync(input);
        await languageServices.OnCompilationFinishedAsync();

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.IsNotNull(diagnosticsText);
        TestContext.WriteLine(diagnosticsText);
        Assert.AreEqual("""
            /test.cs(100,12): error CS0266: Cannot implicitly convert type 'object' to 'string'. An explicit conversion exists (are you missing a cast?)
                string s = o;

            another.cs(200,9): error CS0266: Cannot implicitly convert type 'object' to 'int'. An explicit conversion exists (are you missing a cast?)
                int i = o;
            """.ReplaceLineEndings(), diagnosticsText);

        await VerifyDiagnosticsAsync(languageServices, "test.cs",
        [
            "(3,8): hint IDE0059: Unnecessary assignment of a value to 's'",
            "(3,12): error CS0266: Cannot implicitly convert type 'object' to 'string'. An explicit conversion exists (are you missing a cast?)",
            "(5,5): hint IDE0059: Unnecessary assignment of a value to 'i'",
            "(5,9): error CS0266: Cannot implicitly convert type 'object' to 'int'. An explicit conversion exists (are you missing a cast?)",
        ]);
    }

    [TestMethod]
    public async Task Diagnostics_RenameToScript()
    {
        const string code = "return \"string\";";
        var modelUri = CompiledAssembly.GetInputModelUri("test.cs", guidOverride: uriGuidOverride);

        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();

        await languageServices.OnDidChangeWorkspaceAsync([new(modelUri, "test.cs") { NewContent = code }]);
        await VerifyDiagnosticsAsync(languageServices, "test.cs",
        [
            "(1,8): error CS0029: Cannot implicitly convert type 'string' to 'int'",
        ], modelUri);

        await languageServices.OnDidChangeWorkspaceAsync([new(modelUri, "test.csx") { NewContent = code }]);
        await VerifyDiagnosticsAsync(languageServices, "test.csx", [], modelUri);
    }

    [TestMethod]
    public async Task Diagnostics_PackageSourceGenerator()
    {
        var input = new CompilationInput(new(
        [
            new()
            {
                FileName = "test.cs",
                Text = """
                    #:package CommunityToolkit.Mvvm
                    using System;
                    using CommunityToolkit.Mvvm.ComponentModel;
                    Console.Write(new C { Name = "x" }.Name);
                    partial class C : ObservableObject
                    {
                        [ObservableProperty] private string name = "";
                    }
                    """,
            },
        ]));

        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        var compiled = await compiler.CompileAsync(input);
        compiled.NumErrors.Should().Be(0);
        await languageServices.OnCompilationFinishedAsync();

        var uri = CompiledAssembly.GetInputModelUri("test.cs", guidOverride: uriGuidOverride);
        var markers = await languageServices.GetDiagnosticsAsync(uri);
        TestContext.WriteLine(markers.Select(static m => m.ToDisplayString()).JoinToString("\n"));
        markers.Should().NotContain(static m => m.GetCode() == "CS0117");
    }

    [TestMethod]
    public async Task Diagnostics_BclSourceGenerator()
    {
        var input = new CompilationInput(new(
        [
            new()
            {
                FileName = "test.cs",
                Text = """
                    using System.Text.RegularExpressions;
                    using System;

                    Console.Write(EmailValidator.IsValid("a@b.co"));

                    public static partial class EmailValidator
                    {
                        [GeneratedRegex(@"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$", RegexOptions.IgnoreCase)]
                        private static partial Regex EmailRegex();

                        public static bool IsValid(string input) => EmailRegex().IsMatch(input);
                    }
                    """,
            },
        ]));

        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        var compiled = await compiler.CompileAsync(input);
        compiled.NumErrors.Should().Be(0);
        await languageServices.OnCompilationFinishedAsync();

        var uri = CompiledAssembly.GetInputModelUri("test.cs", guidOverride: uriGuidOverride);
        var markers = await languageServices.GetDiagnosticsAsync(uri);
        TestContext.WriteLine(markers.Select(static m => m.ToDisplayString()).JoinToString("\n"));
        markers.Should().NotContain(static m => m.GetCode() == "CS8795");
    }

    [TestMethod]
    public async Task Diagnostics_RunAnalyzers()
    {
        const string code = """
            using System;
            using System.Runtime.InteropServices;
            using System.Text.RegularExpressions;

            Console.Write(C.Pattern().IsMatch("a"));

            partial class C
            {
                [GeneratedRegex("a")]
                public static partial Regex Pattern();

                [DllImport("not-loaded")]
                public static extern void PInvoke();

                public void M() { int unused; }
            }
            """;
        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        var uri = CompiledAssembly.GetInputModelUri("test.cs", guidOverride: uriGuidOverride);

        foreach (var value in new string?[] { null, "true", "false", null })
        {
            var runAnalyzers = value == "true";
            var input = new CompilationInput(new(
            [
                new() { FileName = "test.cs", Text = code },
                new() { FileName = "Options.cs", Text = value is null ? "" : $"#:property RunAnalyzers={value}" },
            ]));

            await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
            if (value is null)
            {
                var initialMarkers = await languageServices.GetDiagnosticsAsync(uri);
                initialMarkers.Should().NotContain(m => m.GetCode() == "IDE0040");
            }

            var compiled = await compiler.CompileAsync(input);
            compiled.NumErrors.Should().Be(0);
            compiled.Diagnostics.Should().Contain(d => d.Id == "CS0168");
            compiled.Diagnostics.Any(d => d.Id == "SYSLIB1054").Should().Be(runAnalyzers);
            if (runAnalyzers)
            {
                compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text.Should()
                    .Contain("info SYSLIB1054:");
            }
            await languageServices.OnCompilationFinishedAsync();

            var markers = await languageServices.GetDiagnosticsAsync(uri);
            markers.Should().Contain(m => m.GetCode() == "CS0168");
            markers.Any(m => m.GetCode() == "SYSLIB1054").Should().Be(runAnalyzers);
            markers.Any(m => m.GetCode() == "IDE0040").Should().Be(runAnalyzers);
            markers.Should().NotContain(m => m.GetCode() == "CS8795");

            await languageServices.OnDidChangeWorkspaceAsync(
                [new(uri, "test.cs") { NewContent = code + "\nclass Broken { void M() { int x = ; } }" }]);
            markers = await languageServices.GetDiagnosticsAsync(uri);
            markers.Should().Contain(m => m.GetCode() == "CS1525");
        }
    }

    [TestMethod, CombinatorialData]
    public async Task Diagnostics_AdditionalFiles(
        [CombinatorialValues(RazorToolchain.SourceGenerator, RazorToolchain.InternalApi)] RazorToolchain toolchain)
    {
        const string bannedSymbols = "T:System.Console;Not allowed";
        var input = new CompilationInput(new(
        [
            new()
            {
                FileName = "test.cs",
                Text = """
                    #:property RunAnalyzers=true
                    #:package Microsoft.CodeAnalysis.BannedApiAnalyzers@5.6.0
                    System.Console.WriteLine();
                    """,
            },
            new() { FileName = "BannedSymbols.txt", Text = bannedSymbols },
        ]))
        {
            RazorToolchain = toolchain,
        };
        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        var models = ToModelInfos(input);
        var sourceUri = models[0].Uri;
        var additionalUri = models[1].Uri;

        await languageServices.OnDidChangeWorkspaceAsync(models);
        var compiled = await compiler.CompileAsync(input);
        compiled.Diagnostics.Should().Contain(d => d.Id == "RS0030");
        await languageServices.OnCompilationFinishedAsync();
        await verifyAsync(banned: true);

        await languageServices.OnDidChangeModelContentAsync(sourceUri, new()
        {
            Changes = [new() { RangeOffset = 0, RangeLength = 0, Text = "\n" }],
        });
        await verifyAsync(banned: true);

        await languageServices.OnDidChangeModelContentAsync(additionalUri, new()
        {
            Changes = [new() { RangeOffset = 0, RangeLength = bannedSymbols.Length, Text = "" }],
        });
        await verifyAsync(banned: false);

        await languageServices.OnDidChangeModelContentAsync(additionalUri, new()
        {
            Changes = [new() { RangeOffset = 0, RangeLength = 0, Text = bannedSymbols }],
        });
        await verifyAsync(banned: true);

        await languageServices.OnDidChangeWorkspaceAsync(
            [new(sourceUri, "test.cs"), new(additionalUri, "Ignored.txt")]);
        await verifyAsync(banned: false);

        await languageServices.OnDidChangeWorkspaceAsync(
            [new(sourceUri, "test.cs"), new(additionalUri, "BannedSymbols.txt")]);
        await verifyAsync(banned: true);

        await languageServices.OnDidChangeWorkspaceAsync([new(sourceUri, "test.cs")]);
        await verifyAsync(banned: false);

        await languageServices.OnDidChangeWorkspaceAsync(
            [new(sourceUri, "test.cs"), new(additionalUri, "BannedSymbols.txt") { NewContent = bannedSymbols }]);
        await verifyAsync(banned: true);

        async Task verifyAsync(bool banned)
        {
            var markers = await languageServices.GetDiagnosticsAsync(sourceUri);
            markers.Count(m => m.GetCode() == "RS0030").Should().Be(banned ? 1 : 0);
        }
    }

    [TestMethod]
    public async Task Diagnostics_RenameAdditionalFile()
    {
        const string uri = "test";
        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();

        await languageServices.OnDidChangeWorkspaceAsync([new(uri, "test.txt") { NewContent = "#error test" }]);
        var markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().BeEmpty();

        await languageServices.OnDidChangeWorkspaceAsync([new(uri, "test.cs")]);
        markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().ContainSingle(m => m.GetCode() == "CS1029");

        await languageServices.OnDidChangeWorkspaceAsync([new(uri, "test.txt")]);
        markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().BeEmpty();
    }

    [TestMethod, CombinatorialData]
    public async Task CachedConfiguration_CompilerEditorFeatures(bool previousCompilation)
    {
        const string configuration = "Config.CSharpCompilationOptions(options => options.WithAllowUnsafe(true));";
        var input = new CompilationInput(new([new() { FileName = "test.cs", Text = "public class C;" }]))
        {
            Configuration = configuration,
        };
        using var cacheHandler = new MockHttpMessageHandler(TestContext);
        var cacheServices = WorkerServices.CreateTest(TestContext, cacheHandler);
        var cachedOutput = await cacheServices.GetRequiredService<CompilerProxy>().CompileAsync(input);
        Assert.AreEqual(0, cachedOutput.NumErrors);
        using var handler = new MockHttpMessageHandler(TestContext);
        var services = WorkerServices.CreateTest(TestContext, handler, collection =>
        {
            collection.AddScoped<CountingNuGetDownloader>(sp => new(sp.GetRequiredService<NuGetDownloaderPlugin>()));
            collection.AddScoped<INuGetDownloader>(sp => sp.GetRequiredService<CountingNuGetDownloader>());
        });
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        if (previousCompilation)
        {
            await compiler.CompileAsync(input);
            await languageServices.OnCompilationFinishedAsync();
        }

        var uri = CompiledAssembly.GetInputModelUri(CompiledAssembly.ConfigurationFileName, guidOverride: uriGuidOverride);
        await verifyEditorFeaturesAsync();

        await languageServices.OnCachedCompilationLoadedAsync(
            new() { Configuration = configuration }, input, cachedOutput);
        Assert.AreSame(cachedOutput, languageServices.CompilerCache);
        await verifyEditorFeaturesAsync();

        await languageServices.OnDidChangeModelContentAsync(uri, new()
        {
            Changes = [new() { RangeOffset = configuration.Length, RangeLength = 0, Text = " " }],
        });
        await verifyEditorFeaturesAsync();
        Assert.AreEqual(0, services.GetRequiredService<CountingNuGetDownloader>().DownloadCount);

        async Task verifyEditorFeaturesAsync()
        {
            var completionJson = await languageServices.ProvideCompletionItemsAsync(
                uri, new() { LineNumber = 1, Column = "Config.".Length + 1 },
                new() { TriggerKind = BlazorMonaco.Languages.CompletionTriggerKind.Invoke },
                TestContext.CancellationToken);
            var completions = JsonSerializer.Deserialize(completionJson, BlazorMonacoJsonContext.Default.MonacoCompletionList);
            Assert.IsNotNull(completions);
            completions.Suggestions.Should().Contain(item => item.Label == "CSharpCompilationOptions");

            var positionJson = JsonSerializer.Serialize(new Position
            {
                LineNumber = 1,
                Column = configuration.IndexOf("WithAllowUnsafe", StringComparison.Ordinal) + 1,
            }, BlazorMonacoJsonContext.Default.Position);
            var hover = await languageServices.ProvideHoverAsync(uri, positionJson, TestContext.CancellationToken);
            Assert.IsNotNull(hover);
            Assert.Contains("CSharpCompilationOptions", hover);
            Assert.Contains("WithAllowUnsafe", hover);
        }
    }

    [TestMethod, CombinatorialData]
    public async Task Diagnostics_CachedSdkAnalyzersAndGenerators(bool runAnalyzers, bool previousCompilation)
    {
        const string code = """
            using System;
            using System.Runtime.InteropServices;
            using System.Text.RegularExpressions;

            Console.Write(C.Pattern().IsMatch("a"));
            partial class C
            {
                [GeneratedRegex("a")]
                public static partial Regex Pattern();

                [DllImport("not-loaded")]
                public static extern void PInvoke();
            }
            """;
        var input = new CompilationInput(new(
        [
            new() { FileName = "test.cs", Text = code },
            new() { FileName = "Options.cs", Text = $"#:property RunAnalyzers={runAnalyzers}" },
        ]));
        var cacheServices = WorkerServices.CreateTest(TestContext);
        var cachedOutput = await cacheServices.GetRequiredService<CompilerProxy>().CompileAsync(input);
        Assert.AreEqual(0, cachedOutput.NumErrors);
        var services = WorkerServices.CreateTest(TestContext, configureServices: collection =>
        {
            collection.AddScoped<CountingNuGetDownloader>(sp => new(sp.GetRequiredService<NuGetDownloaderPlugin>()));
            collection.AddScoped<INuGetDownloader>(sp => sp.GetRequiredService<CountingNuGetDownloader>());
        });
        var downloads = services.GetRequiredService<CountingNuGetDownloader>();
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        if (previousCompilation)
        {
            await compiler.CompileAsync(new(new(
            [
                new() { FileName = "Before.cs", Text = $"#:property RunAnalyzers={!runAnalyzers}\npublic class Before;" },
            ])));
            await languageServices.OnCompilationFinishedAsync();
        }

        var uri = CompiledAssembly.GetInputModelUri("test.cs", guidOverride: uriGuidOverride);
        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        await languageServices.OnCachedCompilationLoadedAsync(CompilerConfiguration.Empty, input, cachedOutput);
        Assert.AreSame(cachedOutput, languageServices.CompilerCache);
        var markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().NotContain(m => m.GetCode() == "CS8795");

        await languageServices.OnDidChangeModelContentAsync(uri, new()
        {
            Changes = [new() { RangeOffset = 0, RangeLength = 0, Text = "\n" }],
        });
        markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().NotContain(m => m.Severity == MarkerSeverity.Error);
        markers.Any(m => m.GetCode() == "SYSLIB1054").Should().Be(runAnalyzers);
        markers.Any(m => m.GetCode() == "IDE0040").Should().Be(runAnalyzers);
        Assert.AreEqual(0, downloads.DownloadCount);
    }

    [TestMethod]
    public async Task Diagnostics_CachedPackageInputWaitsForCompilation()
    {
        var input = new CompilationInput(new(
        [
            new() { FileName = "test.cs", Text = "using Humanizer;\nSystem.Console.Write(1.ToWords());" },
            new() { FileName = "Packages.cs", Text = "#:package Humanizer.Core@2.14.1" },
        ]));
        var cacheServices = WorkerServices.CreateTest(TestContext);
        var cachedOutput = await cacheServices.GetRequiredService<CompilerProxy>().CompileAsync(input);
        Assert.AreEqual(0, cachedOutput.NumErrors);

        var services = WorkerServices.CreateTest(TestContext, configureServices: collection =>
        {
            collection.AddScoped<CountingNuGetDownloader>(sp => new(sp.GetRequiredService<NuGetDownloaderPlugin>()));
            collection.AddScoped<INuGetDownloader>(sp => sp.GetRequiredService<CountingNuGetDownloader>());
        });
        var downloads = services.GetRequiredService<CountingNuGetDownloader>();
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        var uri = CompiledAssembly.GetInputModelUri("test.cs", guidOverride: uriGuidOverride);

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        await languageServices.OnCachedCompilationLoadedAsync(CompilerConfiguration.Empty, input, cachedOutput);
        Assert.AreSame(cachedOutput, languageServices.CompilerCache);
        var markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().BeEmpty();

        await languageServices.OnDidChangeModelContentAsync(uri, new()
        {
            Changes = [new() { RangeOffset = 0, RangeLength = 0, Text = "#error edited\n" }],
        });
        markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().BeEmpty();

        var editedInput = new CompilationInput(new(
        [
            input.Inputs.Value[0] with { Text = "#error edited\n" + input.Inputs.Value[0].Text },
            input.Inputs.Value[1],
        ]));
        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(editedInput));
        markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().BeEmpty();
        Assert.AreEqual(0, downloads.DownloadCount);

        var compiled = await compiler.CompileAsync(editedInput);
        await languageServices.OnCompilationFinishedAsync();
        Assert.AreEqual(1, downloads.DownloadCount);
        Assert.AreEqual(1, compiled.NumErrors);
        markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().Contain(m => m.GetCode() == "CS1029");
        markers.Should().NotContain(m => m.GetCode() == "CS0246" || m.GetCode() == "CS1061");
    }

    [TestMethod]
    [DataRow("// #:package Humanizer.Core@2.14.1\n#error expected")]
    [DataRow("var text = \"#:package Humanizer.Core@2.14.1\";\n#error expected")]
    public async Task Diagnostics_CachedPackageLikeTextDoesNotDefer(string code)
    {
        var input = new CompilationInput(new([new() { FileName = "test.cs", Text = code }]));
        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        var uri = CompiledAssembly.GetInputModelUri("test.cs", guidOverride: uriGuidOverride);
        var cachedOutput = await compiler.CompileAsync(input);

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        await languageServices.OnCachedCompilationLoadedAsync(CompilerConfiguration.Empty, input, cachedOutput);
        await languageServices.OnDidChangeModelContentAsync(uri, new()
        {
            Changes = [new() { RangeOffset = 0, RangeLength = 0, Text = "\n" }],
        });

        var markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().Contain(m => m.GetCode() == "CS1029");
    }

    [TestMethod, CombinatorialData]
    public async Task Diagnostics_CachedCompilationClearsPackageAnalyzers(bool invalidCachedInput)
    {
        const string code = """
            using System.Threading.Tasks;
            class C
            {
                public async Task<int> M()
                {
                    return await Task.FromResult(1);
                }
            }
            """;
        var cachedInput = new CompilationInput(new([new() { FileName = "test.cs", Text = code }]));
        var packageInput = new CompilationInput(new(
        [
            new()
            {
                FileName = "test.cs",
                Text = "#:property RunAnalyzers=true\n#:package AsyncFixer@1.6.0\n" + code,
            },
        ]));
        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        var uri = CompiledAssembly.GetInputModelUri("test.cs", guidOverride: uriGuidOverride);

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(cachedInput));
        var cachedOutput = await compiler.CompileAsync(cachedInput);
        await languageServices.OnCompilationFinishedAsync();
        var initialActions = await getActionTitlesAsync();

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(packageInput));
        await compiler.CompileAsync(packageInput);
        await languageServices.OnCompilationFinishedAsync();
        var markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().Contain(m => m.GetCode() == "AsyncFixer01");
        var packageActions = await getActionTitlesAsync();
        packageActions.Should().Contain(title => !initialActions.Contains(title));

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(cachedInput));
        // Malformed cached input forces directive parsing to throw.
        var notificationInput = invalidCachedInput
            ? new CompilationInput(new([new() { FileName = "test.cs", Text = null! }]))
            : cachedInput;
        await languageServices.OnCachedCompilationLoadedAsync(CompilerConfiguration.Empty, notificationInput, cachedOutput);
        Assert.AreSame(cachedOutput, languageServices.CompilerCache);
        markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().NotContain(m => m.GetCode() == "AsyncFixer01" || m.GetCode() == "IDE0040");
        (await getActionTitlesAsync()).Should().BeEquivalentTo(initialActions);

        await languageServices.OnDidChangeModelContentAsync(uri, new()
        {
            Changes = [new() { RangeOffset = 0, RangeLength = 0, Text = "#error edited\n" }],
        });
        markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().NotContain(m => m.GetCode() == "AsyncFixer01" || m.GetCode() == "IDE0040");
        markers.Any(m => m.GetCode() == "CS1029").Should().Be(!invalidCachedInput);
        (await getActionTitlesAsync()).Should().BeEquivalentTo(initialActions);

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(packageInput));
        await compiler.CompileAsync(packageInput);
        await languageServices.OnCompilationFinishedAsync();
        markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().Contain(m => m.GetCode() == "AsyncFixer01");

        async Task<string[]> getActionTitlesAsync()
        {
            var json = await languageServices.ProvideCodeActionsAsync(uri, null, TestContext.CancellationToken);
            return JsonSerializer.Deserialize(json!, BlazorMonacoJsonContext.Default.ImmutableArrayMonacoCodeAction)
                .Select(action => action.Title).ToArray();
        }
    }

    [TestMethod, CombinatorialData]
    public async Task Diagnostics_PackageAnalyzerFading(bool warning)
    {
        var input = new CompilationInput(new(
        [
            new()
            {
                FileName = "test.cs",
                Text = """
                    #:property RunAnalyzers=true
                    #:package Roslynator.Analyzers@4.14.1
                    public class C
                    {
                        public bool M(bool value) { int unused; return value && true; }
                    }
                    """,
            },
        ]))
        {
            Configuration = $$"""
                Config.CSharpCompilationOptions(options => options.WithSpecificDiagnosticOptions(
                    options.SpecificDiagnosticOptions.SetItem("RCS1033", ReportDiagnostic.{{(warning ? "Warn" : "Info")}})));
                """,
        };
        using var httpMessageHandler = new MockHttpMessageHandler(TestContext);
        var services = WorkerServices.CreateTest(TestContext, httpMessageHandler);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        var uri = CompiledAssembly.GetInputModelUri("test.cs", guidOverride: uriGuidOverride);

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        var compiled = await compiler.CompileAsync(input);
        compiled.NumErrors.Should().Be(0);
        await languageServices.OnCompilationFinishedAsync();

        var markers = await languageServices.GetDiagnosticsAsync(uri);
        var marker = markers.Should().ContainSingle(m => m.GetCode() == "RCS1033").Which;
        marker.Severity.Should().Be(warning ? MarkerSeverity.Warning : MarkerSeverity.Info);
        marker.Tags.Should().Contain(MarkerTag.Unnecessary);
        compiled.Diagnostics.Should().ContainSingle(d => d.Id == "RCS1033")
            .Which.Tags.Should().Be(DiagnosticTags.Unnecessary);
        compiled.Diagnostics.Should().ContainSingle(d => d.Id == "CS0168")
            .Which.Tags.Should().Be(DiagnosticTags.None);

        await languageServices.OnDidChangeModelContentAsync(uri, new()
        {
            Changes = [new() { RangeOffset = 0, RangeLength = 0, Text = "\n" }],
        });
        markers = await languageServices.GetDiagnosticsAsync(uri);
        marker = markers.Should().ContainSingle(m => m.GetCode() == "RCS1033").Which;
        marker.Severity.Should().Be(warning ? MarkerSeverity.Warning : MarkerSeverity.Hint);
        marker.Tags.Should().Contain(MarkerTag.Unnecessary);
    }

    [TestMethod, CombinatorialData]
    public async Task Diagnostics_PackageAnalyzer(
        [CombinatorialValues(RazorToolchain.SourceGenerator, RazorToolchain.InternalApi)] RazorToolchain toolchain,
        bool runAnalyzers)
    {
        var code = $$"""
            #:property RunAnalyzers={{runAnalyzers}}
            #:package AsyncFixer@1.6.0
            using System.Threading.Tasks;
            public class C
            {
                public async Task<int> M()
                {
                    return await Task.FromResult(1);
                }
            }
            """;
        var input = new CompilationInput(new([new() { FileName = "test.cs", Text = code }]))
        {
            RazorToolchain = toolchain,
        };
        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        var uri = CompiledAssembly.GetInputModelUri("test.cs", guidOverride: uriGuidOverride);

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        var compiled = await compiler.CompileAsync(input);
        compiled.NumErrors.Should().Be(0);
        compiled.NumWarnings.Should().Be(runAnalyzers ? 1 : 0);
        compiled.Diagnostics.Any(d => d.Id == "AsyncFixer01").Should().Be(runAnalyzers);
        await languageServices.OnCompilationFinishedAsync();

        var markers = await languageServices.GetDiagnosticsAsync(uri);
        if (runAnalyzers)
        {
            markers.Should().ContainSingle(m => m.GetCode() == "AsyncFixer01")
                .Which.Severity.Should().Be(MarkerSeverity.Warning);
        }
        else
        {
            markers.Should().NotContain(m => m.GetCode() == "AsyncFixer01");
        }

        var codeActions = await getCodeActionsAsync();
        if (runAnalyzers)
        {
            var action = codeActions.Should().ContainSingle(a => a.Title.Contains("async/await", StringComparison.Ordinal)).Which;
            Assert.IsNotNull(action.Edit);
            action.Edit.Edits.Should().OnlyContain(edit => edit.ResourceUri == uri && edit.TextEdit.Text == "");
            var text = SourceText.From(code);
            action.Edit.Edits.Select(edit => text.ToString(edit.TextEdit.Range.ToSpan(text.Lines)).Trim())
                .Should().BeEquivalentTo(["async", "await"]);
        }
        else
        {
            codeActions.Should().NotContain(a => a.Title.Contains("async/await", StringComparison.Ordinal));
        }

        var fixedCode = code.Replace("async ", "").Replace("await ", "");
        await languageServices.OnDidChangeWorkspaceAsync([new(uri, "test.cs") { NewContent = fixedCode }]);
        markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().NotContain(m => m.GetCode() == "AsyncFixer01");
        (await getCodeActionsAsync()).Should().NotContain(a => a.Title.Contains("async/await", StringComparison.Ordinal));

        input = new CompilationInput(new(
            [new() { FileName = "test.cs", Text = code.Replace("#:package AsyncFixer@1.6.0", "") }]))
        {
            RazorToolchain = toolchain,
        };
        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        compiled = await compiler.CompileAsync(input);
        compiled.NumErrors.Should().Be(0);
        compiled.NumWarnings.Should().Be(0);
        compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text.Should().NotContain("AsyncFixer01");
        await languageServices.OnCompilationFinishedAsync();
        markers = await languageServices.GetDiagnosticsAsync(uri);
        markers.Should().NotContain(m => m.GetCode() == "AsyncFixer01");
        (await getCodeActionsAsync()).Should().NotContain(a => a.Title.Contains("async/await", StringComparison.Ordinal));

        async Task<ImmutableArray<MonacoCodeAction>> getCodeActionsAsync()
        {
            var json = await languageServices.ProvideCodeActionsAsync(uri, null, TestContext.CancellationToken);
            return JsonSerializer.Deserialize(json!, BlazorMonacoJsonContext.Default.ImmutableArrayMonacoCodeAction);
        }
    }

    [TestMethod]
    public async Task SignatureHelp()
    {
        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        var code = """
            C.M();
            class C { public static void M(int x) { } }
            """;
        var file = "test.cs";
        await languageServices.OnDidChangeWorkspaceAsync([new(file, file) { NewContent = code }]);

        var positionJson = JsonSerializer.Serialize(new Position { LineNumber = 1, Column = 5 }, BlazorMonacoJsonContext.Default.Position);
        var contextJson = JsonSerializer.Serialize(new SignatureHelpContext { TriggerKind = SignatureHelpTriggerKind.Invoke, IsRetrigger = false }, BlazorMonacoJsonContext.Default.SignatureHelpContext);
        var signatureHelpJson = await languageServices.ProvideSignatureHelpAsync(file, positionJson, contextJson, TestContext.CancellationToken);
        var signatureHelp = JsonSerializer.Deserialize(signatureHelpJson!, BlazorMonacoJsonContext.Default.SignatureHelp);

        signatureHelp!.Signatures.Should().ContainSingle()
            .Which.Label.Should().Be("void C.M(int x)");
    }

    private sealed class CountingNuGetDownloader(INuGetDownloader inner) : INuGetDownloader
    {
        public int DownloadCount { get; private set; }

        public Task<NuGetResults> DownloadAsync(
            Set<NuGetDependency> dependencies,
            string targetFramework,
            bool loadForExecution,
            Version? compilerRoslynVersion = null)
        {
            DownloadCount++;
            return inner.DownloadAsync(dependencies, targetFramework, loadForExecution, compilerRoslynVersion);
        }
    }
}
