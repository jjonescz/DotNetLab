using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting.Combinatorial;

namespace DotNetLab;

[TestClass]
public sealed class ExecutorTests
{
    public required TestContext TestContext { get; set; }

    [TestMethod, CombinatorialData]
    public async Task AnalyzerErrors_DoNotBlockRunAndRazorRendering(
        [CombinatorialValues(RazorToolchain.SourceGenerator, RazorToolchain.InternalApi)] RazorToolchain toolchain,
        bool runAnalyzers,
        bool analyzerError)
    {
        using var httpMessageHandler = new MockHttpMessageHandler(TestContext);
        var services = WorkerServices.CreateTest(TestContext, httpMessageHandler);
        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new(
            [
                new()
                {
                    FileName = "Input.cs",
                    Text = $$"""
                        #:property RunAnalyzers={{runAnalyzers}}
                        #:package Microsoft.CodeAnalysis.BannedApiAnalyzers@5.6.0
                        System.Console.Write("executed");
                        """,
                },
                new() { FileName = "BannedSymbols.txt", Text = "T:System.Console;Not allowed" },
                new() { FileName = "TestComponent.razor", Text = "<div>rendered</div>" },
            ]))
            {
                RazorToolchain = toolchain,
                Configuration = $$"""
                    Config.CSharpCompilationOptions(options => options.WithSpecificDiagnosticOptions(
                        options.SpecificDiagnosticOptions.SetItem("RS0030", ReportDiagnostic.{{(analyzerError ? "Error" : "Warn")}})));
                    """,
            });

        Assert.AreEqual(runAnalyzers && analyzerError ? 1 : 0, compiled.NumErrors);
        Assert.AreEqual(runAnalyzers && !analyzerError ? 1 : 0, compiled.NumWarnings);
        var diagnostics = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.IsNotNull(diagnostics);
        if (runAnalyzers)
        {
            Assert.Contains(analyzerError ? "error RS0030:" : "warning RS0030:", diagnostics);
        }
        else
        {
            Assert.AreEqual(string.Empty, diagnostics);
        }

        var run = (await compiled.GetRequiredGlobalOutput("run").LoadAsync()).Text;
        var html = (await compiled.GetRequiredOutput("TestComponent.razor", "html").LoadAsync()).Text;
        TestContext.WriteLine(run);
        TestContext.WriteLine(html);
        Assert.AreEqual("""
            Exit code: 0
            Stdout:
            executed
            Stderr:

            """.ReplaceLineEndings("\n"), run);
        Assert.AreEqual("<div>rendered</div>", html);
    }

    [TestMethod, CombinatorialData]
    public async Task WarningsAsErrors_DoNotBlockRunAndRazorRendering(
        [CombinatorialValues(RazorToolchain.SourceGenerator, RazorToolchain.InternalApi)] RazorToolchain toolchain,
        bool specific)
    {
        using var httpMessageHandler = new MockHttpMessageHandler(TestContext);
        var services = WorkerServices.CreateTest(TestContext, httpMessageHandler);
        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new(
            [
                new()
                {
                    FileName = "Input.cs",
                    Text = """
                        System.Console.Write("executed");
                        public class C
                        {
                            #pragma warning disable CS0169
                            private int unusedField;
                            public void M() { int unused; }
                        }
                        """,
                },
                new() { FileName = "TestComponent.razor", Text = "<div>rendered</div>" },
            ]))
            {
                RazorToolchain = toolchain,
                Configuration = specific ? """
                    Config.CSharpCompilationOptions(options => options.WithSpecificDiagnosticOptions(
                        options.SpecificDiagnosticOptions.SetItem("CS0168", ReportDiagnostic.Error)));
                    """ : """
                    Config.CSharpCompilationOptions(options => options.WithGeneralDiagnosticOption(ReportDiagnostic.Error));
                    """,
            });

        Assert.AreEqual(1, compiled.NumErrors);
        Assert.AreEqual(0, compiled.NumWarnings);
        var diagnostic = Assert.ContainsSingle(compiled.Diagnostics.Where(d => d.Severity == DiagnosticDataSeverity.Error));
        Assert.AreEqual("CS0168", diagnostic.Id);
        var diagnostics = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.IsNotNull(diagnostics);
        Assert.Contains("error CS0168:", diagnostics);
        Assert.Contains("void M (", (await compiled.GetRequiredGlobalOutput("il").LoadAsync()).Text);
        Assert.AreEqual("""
            Exit code: 0
            Stdout:
            executed
            Stderr:

            """.ReplaceLineEndings("\n"), (await compiled.GetRequiredGlobalOutput("run").LoadAsync()).Text);
        Assert.AreEqual("<div>rendered</div>", (await compiled.GetRequiredOutput("TestComponent.razor", "html").LoadAsync()).Text);
    }

    [TestMethod, CombinatorialData]
    public async Task CompilerErrors_BlockRunAndRazorRendering(
        [CombinatorialValues(RazorToolchain.SourceGenerator, RazorToolchain.InternalApi)] RazorToolchain toolchain)
    {
        using var httpMessageHandler = new MockHttpMessageHandler(TestContext);
        var services = WorkerServices.CreateTest(TestContext, httpMessageHandler);
        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new(
            [
                new()
                {
                    FileName = "Input.cs",
                    Text = """
                        System.Console.MissingMethod();
                        public class C { public void M() { int unused; } }
                        """,
                },
                new() { FileName = "TestComponent.razor", Text = "<div>rendered</div>" },
            ]))
            {
                RazorToolchain = toolchain,
                Configuration = """
                    Config.CSharpCompilationOptions(options => options.WithGeneralDiagnosticOption(ReportDiagnostic.Error));
                    """,
            });

        Assert.AreEqual(2, compiled.NumErrors);
        Assert.AreEqual(0, compiled.NumWarnings);
        Assert.IsEmpty((await compiled.GetRequiredGlobalOutput("il").LoadAsync()).Text);
        Assert.Contains("error CS0117:", (await compiled.GetRequiredGlobalOutput("run").LoadAsync()).Text);
        Assert.Contains("error CS0117:", (await compiled.GetRequiredOutput("TestComponent.razor", "html").LoadAsync()).Text);
    }

    [TestMethod, CombinatorialData]
    public async Task AsyncMain(bool args)
    {
        var services = WorkerServices.CreateTest(TestContext);

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
        Assert.IsNotNull(diagnosticsText);
        TestContext.WriteLine(diagnosticsText);
        Assert.AreEqual(string.Empty, diagnosticsText);

        var runText = (await compiled.GetRequiredGlobalOutput("run").LoadAsync()).Text;
        TestContext.WriteLine(runText);
        Assert.AreEqual("""
            Exit code: 42
            Stdout:
            Hello.
            Stderr:

            """.ReplaceLineEndings("\n"), runText);
    }

    [TestMethod]
    [DataRow("\"Hello result\"")]
    [DataRow("123")]
    [DataRow("true")]
    [DataRow("null")]
    public async Task ScriptReturnValue_PrintsFormattedValue(string value)
    {
        var services = WorkerServices.CreateTest(TestContext);

        string code = $"""
            return {value};
            """;

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.csx", Text = code }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.IsNotNull(diagnosticsText);
        TestContext.WriteLine(diagnosticsText);
        Assert.AreEqual(string.Empty, diagnosticsText);

        var runText = (await compiled.GetRequiredGlobalOutput("run").LoadAsync()).Text;
        TestContext.WriteLine(runText);
        Assert.AreEqual(string.Format("""
            Exit code: 0
            Stdout:
            {0}
            Stderr:

            """.ReplaceLineEndings("\n"), $"""
            {value}

            """.ReplaceLineEndings()), runText);
    }

    [TestMethod, CombinatorialData]
    public async Task AsyncMain_TopLevel(bool script)
    {
        var services = WorkerServices.CreateTest(TestContext);

        string code = /* lang=C# */ """
            using System;
            using System.Threading.Tasks;
            Console.Write("Hello.");
            await Task.Delay(1);
            return 42;
            """;

        string ext = script ? "csx" : "cs";

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = $"Input.{ext}", Text = code }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.IsNotNull(diagnosticsText);
        TestContext.WriteLine(diagnosticsText);
        Assert.AreEqual(string.Empty, diagnosticsText);

        var runText = (await compiled.GetRequiredGlobalOutput("run").LoadAsync()).Text;
        TestContext.WriteLine(runText);
        var expectedExitCode = script ? 0 : 42;
        var expectedStdout = script
            ? $"""
                Hello.42

                """.ReplaceLineEndings()
            : "Hello.";
        Assert.AreEqual(string.Format($$"""
            Exit code: {{expectedExitCode}}
            Stdout:
            {0}
            Stderr:

            """.ReplaceLineEndings("\n"), expectedStdout), runText);
    }

    /// <summary>
    /// Simulate a scenario that can happen in the app: the user's code is executing
    /// while the app's code logs some messages (e.g., IDE is logging info about semantic colorization which is happening in parallel).
    /// The app's logs should not be captured into user's code output.
    /// </summary>
    [TestMethod]
    public async Task AsyncCapture_Logging()
    {
        var services = WorkerServices.CreateTest(TestContext, configureServices: static services =>
        {
            services.AddLogging(static builder =>
            {
                // We only really care about this provider which is used by the background worker.
                // The app itself shouldn't be logging anything while user's code is executing.
                builder.AddProvider(new SimpleConsoleLoggerProvider()); 
            });
        });

        var logger = services.GetRequiredService<ILogger<ExecutorTests>>();

        var (stdout, stderr) = await Util.CaptureConsoleOutputAsync(async () =>
        {
            logger.LogError("log");
            Console.Write("console");
        });

        Assert.AreEqual("console", stdout);
        Assert.AreEqual(string.Empty, stderr);
    }

    /// <summary>
    /// <see href="https://github.com/jjonescz/DotNetLab/issues/132"/>
    /// </summary>
    [TestMethod]
    public async Task Culture()
    {
        var services = WorkerServices.CreateTest(TestContext);

        var source = /* lang=C# */ """
            using System;
            using System.Globalization;
            using System.Collections.Generic;

            var amount = 123.45;
            List<CultureInfo> cultures = [CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("de-DE")];

            foreach (var culture in  cultures)
            {
                CultureInfo.CurrentCulture = culture;
                Console.Write($"{culture}: ");
                Console.Write($"{amount} ");
                Console.Write("{0} ", amount);
            }
            """;

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.cs", Text = source }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.IsNotNull(diagnosticsText);
        TestContext.WriteLine(diagnosticsText);
        Assert.AreEqual(string.Empty, diagnosticsText);

        var runText = (await compiled.GetRequiredGlobalOutput("run").LoadAsync()).Text;
        TestContext.WriteLine(runText);
        Assert.AreEqual("""
            Exit code: 0
            Stdout:
            en-US: 123.45 123.45 de-DE: 123,45 123,45 
            Stderr:

            """.ReplaceLineEndings("\n"), runText);
    }

    [TestMethod]
    public async Task Exception_Sync()
    {
        var services = WorkerServices.CreateTest(TestContext);

        var source = /* lang=C# */ """
            using System;

            M();

            static void M()
            {
                throw new Exception("test");
            }
            """;

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.cs", Text = source }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.IsNotNull(diagnosticsText);
        TestContext.WriteLine(diagnosticsText);
        Assert.AreEqual(string.Empty, diagnosticsText);

        var runText = (await compiled.GetRequiredGlobalOutput("run").LoadAsync()).Text;
        TestContext.WriteLine(runText);
        Assert.StartsWith(string.Format("""
            Exit code: -532462766
            Stdout:

            Stderr:
            {0}
            """.ReplaceLineEndings("\n"), """
            Unhandled exception. System.Exception: test
               at Program.<<Main>$>g__M|0_0()
               at Program.<Main>$(String[] args)

            """.ReplaceLineEndings()), runText);
    }

    [TestMethod]
    public async Task Exception_Async()
    {
        var services = WorkerServices.CreateTest(TestContext);

        var source = /* lang=C# */ """
            using System;
            using System.Threading.Tasks;

            await M();

            static async Task M()
            {
                throw new Exception("test");
            }
            """;

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.cs", Text = source }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.IsNotNull(diagnosticsText);
        TestContext.WriteLine(diagnosticsText);
        Assert.AreEqual(string.Empty, diagnosticsText);

        var runText = (await compiled.GetRequiredGlobalOutput("run").LoadAsync()).Text;
        TestContext.WriteLine(runText);
        Assert.StartsWith(string.Format("""
            Exit code: -532462766
            Stdout:

            Stderr:
            {0}
            """.ReplaceLineEndings("\n"), """
            Unhandled exception. System.Exception: test
               at Program.<<Main>$>g__M|0_0()
               at Program.<Main>$(String[] args)

            """.ReplaceLineEndings()), runText);
    }
}
