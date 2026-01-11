using AwesomeAssertions;
using BlazorMonaco;
using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;
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
        ]));

        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();

        var input = getInput("v1");
        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        await compiler.CompileAsync(input);
        languageServices.OnCompilationFinished();

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

    private async Task VerifyDiagnosticsAsync(ILanguageServices languageServices, string file, string[] expectedDiagnostics)
    {
        var uri = CompiledAssembly.GetInputModelUri(file, guidOverride: uriGuidOverride);
        var markers = await languageServices.GetDiagnosticsAsync(uri);
        var actualDiagnostics = markers
            .OrderBy(m => (m.StartLineNumber, m.StartColumn, m.Severity, m.GetCode(), m.Message))
            .Select(static m => m.ToDisplayString())
            .ToArray();

        TestContext.WriteLine($"Actual diagnostics for '{file}' ({actualDiagnostics.Length}):");
        TestContext.WriteLine(actualDiagnostics.JoinToString("\n"));

        actualDiagnostics.Should().Equal(expectedDiagnostics);
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
        ]))
        {
            RazorToolchain = toolchain,
        };

        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        await compiler.CompileAsync(input);
        languageServices.OnCompilationFinished();

        await VerifyDiagnosticsAsync(languageServices, "Input.razor",
        [
            "(1,2): hint CS8019: Unnecessary using directive.",
            "(2,11): warning CS0219: The variable 's' is assigned but its value is never used",
            "(2,15): warning CS8600: Converting null literal or possible null value to non-nullable type.",
        ]);

        await VerifyDiagnosticsAsync(languageServices, "other.cs",
        [
            "(1,1): hint CS8019: Unnecessary using directive.",
            "(1,1): hint IDE0005: Using directive is unnecessary.",
            "(1,1): hint RemoveUnnecessaryImportsFixable: ",
        ]);
    }

    [TestMethod]
    public async Task Diagnostics_Configuration()
    {
        var input = new CompilationInput(new(
        [
            new() { FileName = "A.cs", Text = "using System.Linq;" },
            new() { FileName = "Z.cs", Text = "string s = null;" },
        ]))
        {
            Configuration = "void F() { }",
        };

        var services = WorkerServices.CreateTest(TestContext, new MockHttpMessageHandler(TestContext));
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();

        await languageServices.OnDidChangeWorkspaceAsync(ToModelInfos(input));
        await compiler.CompileAsync(input);
        languageServices.OnCompilationFinished();

        await VerifyDiagnosticsAsync(languageServices, "A.cs",
        [
            "(1,1): hint CS8019: Unnecessary using directive.",
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
        languageServices.OnCompilationFinished();

        await VerifyDiagnosticsAsync(languageServices, "test.cs",
        [
            "(3,24): warning CS0626: Method, operator, or accessor 'C.M1()' is marked external and has no attributes on it. Consider adding a DllImport attribute to specify the external implementation.",
        ]);
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
}
