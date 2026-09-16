using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab;

[TestClass]
// JitInspect's process-wide caches are not thread-safe.
[DoNotParallelize]
public sealed class JitDisassemblerTests
{
    public required TestContext TestContext { get; set; }

    [TestInitialize]
    public void Initialize()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Temporarily skipped on Linux: JitInspect writes a multi-GB full-process dump per method, slowing down the tests a lot.");
        }
    }

    [TestMethod]
    public async Task AsmDecompilation()
    {
        await DisassembleAsync("""
            System.Console.WriteLine("Hello");
            """);
    }

    [TestMethod]
    [DataRow("Debug", true)]
    [DataRow("Release", true)]
    [DataRow("Debug", false)]
    [DataRow("Release", false)]
    public async Task AsyncAsmDecompilation(string configuration, bool runtimeAsync)
    {
        var asmText = await DisassembleAsync($$"""
            #:property Features=$(Features);runtime-async={{(runtimeAsync ? "on" : "off")}}
            #:property Configuration={{configuration}}

            using System;
            using System.Threading.Tasks;

            class Program
            {
                static async Task Main()
                {
                    await M();
                    Console.WriteLine("Hello.");
                }

                static async Task M()
                {
                    await Task.Yield();
                }
            }
            """);

        foreach (var method in new[] { "Main", "M" })
        {
            AssertMethodBody(asmText, $"Program.{method}()");
        }
    }

    [TestMethod]
    [DataRow("Task", "")]
    [DataRow("Task<int>", "return 42;")]
    [DataRow("ValueTask", "")]
    [DataRow("ValueTask<int>", "return 42;")]
    public async Task RuntimeAsyncBodyIncludesUserCode(string returnType, string returnStatement)
    {
        var asmText = await DisassembleAsync($$"""
            #:property Features=$(Features);runtime-async=on
            #:property Configuration=Release

            using System;
            using System.Runtime.CompilerServices;
            using System.Threading.Tasks;

            class Program
            {
                static Program() => throw new InvalidOperationException("User code must not execute.");

                [MethodImpl(MethodImplOptions.NoInlining)]
                static void AfterAwait() { }

                static async {{returnType}} M()
                {
                    await Task.Yield();
                    AfterAwait();
                    {{returnStatement}}
                }
            }
            """);

        var body = AssertMethodBody(asmText, "Program.M()");
        Assert.Contains("Program.AfterAwait()", body);
    }

    [TestMethod]
    [DataRow("class", "virtual")]
    [DataRow("struct", "")]
    [DataRow("interface", "")]
    public async Task RuntimeAsyncInstanceMethods(string typeKind, string modifier)
    {
        var asmText = await DisassembleAsync($$"""
            #:property Features=$(Features);runtime-async=on
            #:property Configuration=Release

            using System.Runtime.CompilerServices;
            using System.Threading.Tasks;

            {{typeKind}} Program
            {
                [MethodImpl(MethodImplOptions.NoInlining)]
                static void AfterAwait() { }

                public {{modifier}} async Task<int> M()
                {
                    await Task.Yield();
                    AfterAwait();
                    return 42;
                }

                public async Task<int> M(int value)
                {
                    await M();
                    AfterAwait();
                    return value;
                }
            }
            """);

        foreach (var signature in new[] { "Program.M()", "Program.M(Int32)" })
        {
            Assert.Contains("Program.AfterAwait()", AssertMethodBody(asmText, signature));
        }
    }

    [TestMethod]
    public async Task RuntimeAsyncVoidMethod()
    {
        var asmText = await DisassembleAsync("""
            #:property Features=$(Features);runtime-async=on
            using System.Threading.Tasks;
            class Program
            {
                static async void M()
                {
                    await Task.Yield();
                }
            }
            """);

        AssertMethodBody(asmText, "Program.M()");
    }

    private static string AssertMethodBody(string asmText, string signature)
    {
        var match = Regex.Match(asmText,
            $@"(?ms)^; {Regex.Escape(signature)}\r?\n(?<instructions>.*?)^; Total bytes of code (?<size>\d+)");
        Assert.IsTrue(match.Success, $"Missing disassembly for {signature}.");
        Assert.AreNotEqual("0", match.Groups["size"].Value, $"Empty disassembly for {signature}.");
        return match.Groups["instructions"].Value;
    }

    private async Task<string> DisassembleAsync(string code)
    {
        var services = WorkerServices.CreateTest(TestContext, configureServices: static services =>
        {
            services.AddScoped<IJitAsmDisassembler, JitAsmDisassembler>();
        });

        var compiled = await services.GetRequiredService<CompilerProxy>()
            .CompileAsync(new(new([new() { FileName = "Input.cs", Text = code }])));

        var diagnosticsText = compiled.GetRequiredGlobalOutput(CompiledAssembly.DiagnosticsOutputType).Text;
        Assert.IsNotNull(diagnosticsText);
        TestContext.WriteLine(diagnosticsText);
        Assert.AreEqual(string.Empty, diagnosticsText);

        var asmText = (await compiled.GetRequiredGlobalOutput("asm").LoadAsync()).Text;
        TestContext.WriteLine(asmText);
        Assert.DoesNotContain("JIT disassembler is not available.", asmText);
        Assert.DoesNotContain("Failed to find JIT output.", asmText);
        Assert.DoesNotContain("Exception:", asmText);
        return asmText;
    }
}
