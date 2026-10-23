using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Runtime.InteropServices;

namespace DotNetLab;

[TestClass]
[DoNotParallelize]
public sealed class JitDisassemblerTests
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    public async Task AsmDecompilation()
    {
        var text = await DisassembleAsync("""
            System.Console.WriteLine("Hello");
            """);
        AssertMethodBody(text, "Program.<Main>$(String[])");
        Assert.Contains("System.Console.WriteLine(String)", text);
    }

    [TestMethod]
    public async Task DisassemblyDoesNotExecuteUserCode()
    {
        var text = await DisassembleAsync("""
            class Program
            {
                static Program() => throw new System.InvalidOperationException("Must not execute.");
                public Program() => throw new System.InvalidOperationException("Must not execute.");
                static int M() => 42;
            }
            """);

        AssertMethodBody(text, "Program..cctor()");
        AssertMethodBody(text, "Program..ctor()");
        Assert.Contains("ret", AssertMethodBody(text, "Program.M()"));
    }

    [TestMethod]
    public async Task UnsupportedMethodsHaveDiagnostics()
    {
        var text = await DisassembleAsync("""
            using System.Runtime.InteropServices;
            abstract class Program<T>
            {
                public abstract void Abstract();
                public static T Identity(T value) => value;
            }
            class Native
            {
                [DllImport("not-loaded")]
                public static extern void PInvoke();
                public static void CallsPInvoke() => PInvoke();
                public static U Generic<U>(U value) => value;
                public static int M() => 42;
            }
            abstract class Abstract { public abstract void M(); }
            delegate void Callback();
            """);

        Assert.Contains("; Open generics cannot be JIT-compiled.", text);
        Assert.Contains("; P/Invoke methods cannot be JIT-compiled.", text);
        Assert.Contains("; Abstract methods cannot be JIT-compiled.", text);
        Assert.Contains("; Runtime-implemented methods cannot be JIT-compiled.", text);
        AssertMethodBody(text, "Native.M()");
        AssertMethodBody(text, "Native.CallsPInvoke()");
    }

    [TestMethod]
    public void GenericInstantiations()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            using DotNetLab;
            [assembly: MethodInstantiation("Program`1", "M", new[] { typeof(long) }, new[] { typeof(int) })]
            [GenericArguments(typeof(int))]
            class Program<T>
            {
                [GenericArguments(typeof(int))]
                [GenericArguments(typeof(string))]
                [GenericArguments(typeof(object))]
                public static U M<U>(U value) => value;
            }
            """);
        var references = RefAssemblies.All.Select(r => MetadataReference.CreateFromImage(r.Bytes))
            .Append(MetadataReference.CreateFromFile(typeof(GenericArgumentsAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create("GenericDisassembly", [tree], references,
            new(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        stream.Position = 0;
        var text = new JitAsmDisassembler().Disassemble(stream, []);
        TestContext.WriteLine(text);
        Assert.DoesNotContain("Exception:", text);

        AssertMethodBody(text, "Program`1[System.Int32].M<Int32>(Int32)");
        AssertMethodBody(text, "Program`1[System.Int32].M<String>(String)");
        AssertMethodBody(text, "Program`1[System.Int32].M<Object>(Object)");
        AssertMethodBody(text, "Program`1[System.Int64].M<Int32>(Int32)");
    }

    [TestMethod]
    public async Task ConcurrentDisassemblySessions()
    {
        var texts = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(i => Task.Run(() => DisassembleAsync($"class Program {{ static int M() => {40 + i}; }}"))));
        foreach (var text in texts)
        {
            AssertMethodBody(text, "Program.M()");
        }
    }

    [TestMethod]
    [DataRow(Architecture.X64)]
    [DataRow(Architecture.X86)]
    [DataRow(Architecture.Arm64)]
    public void InstructionDecodersResolveSymbolsAndLabels(Architecture architecture)
    {
        byte[] bytes = architecture == Architecture.Arm64
            ? [0x02, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00, 0x00, 0xfe, 0x03, 0x00, 0x94, 0xc0, 0x03, 0x5f, 0xd6]
            : [0xeb, 0x01, 0x90, 0xe8, 0xf8, 0x0f, 0x00, 0x00, 0xc3];
        var disassembler = new JitCodeDisassembler(new() { [0x2000] = "Target()" });
        using var writer = new StringWriter();
        disassembler.Write(writer, 0x1000, bytes, architecture);
        var text = writer.ToString();

        Assert.Contains("Target()", text);
        Assert.Contains(architecture == Architecture.Arm64 ? "LL_01:" : "L00:", text);
        Assert.Contains("ret", text);
        Assert.Contains($"; Total bytes of code {bytes.Length}", text);
    }

    [TestMethod]
    [DataRow(Architecture.X64)]
    [DataRow(Architecture.Arm64)]
    public void SharedGenericEntryStubs(Architecture architecture)
    {
        var memory = Marshal.AllocHGlobal(128);
        try
        {
            var bytes = new byte[128];
            if (architecture == Architecture.X64)
            {
                new byte[] { 0xff, 0x25, 10, 0, 0, 0 }.CopyTo(bytes, 0);
                new byte[] { 0x48, 0xb9 }.CopyTo(bytes, 32);
                BitConverter.GetBytes(0x1234UL).CopyTo(bytes, 34);
                new byte[] { 0x48, 0xb8 }.CopyTo(bytes, 42);
                BitConverter.GetBytes((ulong)(memory + 80)).CopyTo(bytes, 44);
                new byte[] { 0x48, 0x8b, 0x00, 0xff, 0xe0 }.CopyTo(bytes, 52);
                BitConverter.GetBytes((ulong)(memory + 32)).CopyTo(bytes, 16);
                bytes[100] = 0xc3;
            }
            else
            {
                BitConverter.GetBytes(0x58000050u).CopyTo(bytes, 0); // ldr x16, #8
                BitConverter.GetBytes(0xd61f0200u).CopyTo(bytes, 4); // br x16
                BitConverter.GetBytes((ulong)(memory + 32)).CopyTo(bytes, 8);
                BitConverter.GetBytes(0x5800008bu).CopyTo(bytes, 32); // ldr x11, #16
                BitConverter.GetBytes(0x580000b0u).CopyTo(bytes, 36); // ldr x16, #20
                BitConverter.GetBytes(0xf9400210u).CopyTo(bytes, 40); // ldr x16, [x16]
                BitConverter.GetBytes(0xd61f0200u).CopyTo(bytes, 44); // br x16
                BitConverter.GetBytes(0x1234UL).CopyTo(bytes, 48);
                BitConverter.GetBytes((ulong)(memory + 80)).CopyTo(bytes, 56);
                BitConverter.GetBytes(0xd65f03c0u).CopyTo(bytes, 100); // ret
            }

            BitConverter.GetBytes((ulong)(memory + 100)).CopyTo(bytes, 80);
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            Assert.AreEqual((ulong)(memory + 100), RuntimeEntryPoint.Resolve((ulong)memory, 0x1234, generic: true, architecture));
            Assert.AreEqual((ulong)(memory + 32), RuntimeEntryPoint.Resolve((ulong)memory, 0x5678, generic: true, architecture));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
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
