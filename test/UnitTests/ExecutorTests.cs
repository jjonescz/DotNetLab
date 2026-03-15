using Combinatorial.MSTest;
using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab;

[TestClass]
public sealed class ExecutorTests
{
    public required TestContext TestContext { get; set; }

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

    [TestMethod, CombinatorialData]
    public async Task AsyncMain_TopLevel(bool script)
    {
        var services = WorkerServices.CreateTest(TestContext);

        string code = """
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
        Assert.AreEqual("""
            Exit code: 42
            Stdout:
            Hello.
            Stderr:

            """.ReplaceLineEndings("\n"), runText);
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
}
