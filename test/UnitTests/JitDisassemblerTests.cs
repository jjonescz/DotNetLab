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
    public async Task StateMachineAttributeConstructorsDoNotExecute()
    {
        var key = "StateMachineAttribute_" + Guid.NewGuid().ToString("N");
        try
        {
            var text = await DisassembleAsync($$"""
            using System;
            using System.Runtime.CompilerServices;
            class SubmittedAttribute : StateMachineAttribute
            {
                public SubmittedAttribute() : base(typeof(Program))
                {
                    AppDomain.CurrentDomain.SetData("{{key}}", true);
                    throw new InvalidOperationException("Attribute constructor executed.");
                }
            }
            class Program
            {
                [Submitted]
                public static int M() => 42;
            }
            """);

            Assert.IsNull(AppDomain.CurrentDomain.GetData(key), "The submitted attribute constructor ran.");
            AssertMethodBody(text, "Program.M()");
            AssertMethodBody(text, "SubmittedAttribute..ctor()");
        }
        finally
        {
            AppDomain.CurrentDomain.SetData(key, null);
        }
    }

    [TestMethod]
    [DataRow("type")]
    [DataRow("method")]
    [DataRow("type-null-element")]
    [DataRow("method-null-element")]
    public void InvalidGenericArgumentsHints(string target)
    {
        bool genericType = target.StartsWith("type", StringComparison.Ordinal);
        var hints = $$"""
            {{(target.EndsWith("null-element", StringComparison.Ordinal) ? "[GenericArguments(typeof(int), null)]" : "[GenericArguments(typeof(string))]")}}
            [GenericArguments(typeof(int))]
            """;
        var text = DisassembleSource($$"""
            using DotNetLab;
            {{(genericType ? hints : "")}}
            class Program{{(genericType ? "<T> where T : struct" : "")}}
            {
                {{(!genericType ? hints : "")}}
                public static {{(genericType ? "T M(T value)" : "T M<T>(T value) where T : struct")}} => value;
            }
            class Healthy { public static int M() => 42; }
            """, allowDisassemblyErrors: true);

        Assert.Contains("; Cannot instantiate", text);
        Assert.Contains(target == "method-null-element" ? "; System.ArgumentNullException:" : "; System.ArgumentException:", text);
        AssertMethodBody(text, genericType
            ? "Program`1[System.Int32].M(Int32)" : "Program.M<Int32>(Int32)");
        AssertMethodBody(text, "Healthy.M()");
    }

    [TestMethod]
    [DataRow("missing-type")]
    [DataRow("type-constraint")]
    [DataRow("method-constraint")]
    [DataRow("missing-method")]
    [DataRow("method-arity")]
    [DataRow("malformed-type-name")]
    public void InvalidMethodInstantiationHints(string failure)
    {
        var invalid = failure switch
        {
            "missing-type" => """[assembly: MethodInstantiation("Missing", "M", new System.Type[] {}, new[] { typeof(int) })]""",
            "type-constraint" => """[assembly: MethodInstantiation("G`1", "M", new[] { typeof(string) }, new System.Type[] {})]""",
            "missing-method" => """[assembly: MethodInstantiation("Program", "Missing", new System.Type[] {}, new[] { typeof(int) })]""",
            "method-arity" => """[assembly: MethodInstantiation("Program", "M", new System.Type[] {}, new System.Type[] {})]""",
            "malformed-type-name" => """[assembly: MethodInstantiation("Program", "M", new string[] {}, new[] { "[" })]""",
            _ => """[assembly: MethodInstantiation("Program", "M", new System.Type[] {}, new[] { typeof(string) })]""",
        };
        var text = DisassembleSource($$"""
            using DotNetLab;
            {{invalid}}
            [assembly: MethodInstantiation("Program", "M", new System.Type[] {}, new[] { typeof(int) })]
            class G<T> where T : struct { public static T M(T value) => value; }
            class Program { public static T M<T>(T value) where T : struct => value; }
            class Healthy { public static int M() => 42; }
            """, allowDisassemblyErrors: true);

        Assert.Contains("; Cannot instantiate", text);
        Assert.Contains(failure == "missing-type" ? "; System.TypeLoadException:" : "; System.ArgumentException:", text);
        AssertMethodBody(text, "Program.M<Int32>(Int32)");
        AssertMethodBody(text, "Healthy.M()");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void MethodInstantiationOverloadsAreIsolated(bool assemblyHint)
    {
        var text = DisassembleSource($$"""
            using DotNetLab;
            {{(assemblyHint ? """[assembly: MethodInstantiation("Program", "M", new System.Type[] {}, new[] { typeof(int) })]""" : "")}}
            {{(!assemblyHint ? """[MethodInstantiation("M", new[] { typeof(int) })]""" : "")}}
            class Program
            {
                public static void M<T>(T[] value) where T : class { }
                public static void M<T>(T value) where T : struct { }
                public static void M<T>(System.Collections.Generic.List<T> value) where T : class { }
            }
            class Healthy { public static int M() => 42; }
            """, allowDisassemblyErrors: true);

        Assert.AreEqual(2, Regex.Count(text, "(?m)^; Cannot instantiate overload "));
        AssertMethodBody(text, "Program.M<Int32>(Int32)");
        AssertMethodBody(text, "Healthy.M()");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void MethodInstantiationStringArguments(bool assemblyHint)
    {
        var text = DisassembleSource($$"""
            using DotNetLab;
            {{(assemblyHint ? """[assembly: MethodInstantiation("Program`1", "M", new[] { "System.Int32" }, new[] { "System.Int64" })]""" : "")}}
            [GenericArguments(typeof(int))]
            {{(!assemblyHint ? """[MethodInstantiation("M", new[] { "System.Int64" })]""" : "")}}
            class Program<T> { public static U M<U>(U value) => value; }
            """);

        AssertMethodBody(text, "Program`1[System.Int32].M<Int64>(Int64)");
    }

    [TestMethod]
    [DataRow("async")]
    [DataRow("iterator")]
    [DataRow("async-iterator")]
    public void GenericStateMachineAttributes(string kind)
    {
        var (returnType, body) = kind switch
        {
            "async" => ("async System.Threading.Tasks.Task<U>", "await System.Threading.Tasks.Task.Yield(); return default;"),
            "iterator" => ("System.Collections.Generic.IEnumerable<U>", "yield return default;"),
            _ => ("async System.Collections.Generic.IAsyncEnumerable<U>", "await System.Threading.Tasks.Task.Yield(); yield return default;"),
        };
        var text = DisassembleSource($$"""
            using DotNetLab;
            [GenericArguments(typeof(int))]
            class Program<T>
            {
                [GenericArguments(typeof(long))]
                public static {{returnType}} M<U>() { {{body}} }
            }
            """);

        var match = Regex.Match(text, @"(?m)^; (?<signature>Program[^\r\n]*\.MoveNext\(\))\r?$");
        Assert.IsTrue(match.Success, "Missing the constructed state-machine body.");
        Assert.Contains("System.Int32", match.Value);
        Assert.Contains("System.Int64", match.Value);
        AssertMethodBody(text, match.Groups["signature"].Value);
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
        var text = DisassembleSource("""
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

    [TestMethod]
    [DataRow("Task", "method")]
    [DataRow("Task", "type")]
    [DataRow("Task", "both")]
    [DataRow("ValueTask", "method")]
    [DataRow("ValueTask", "type")]
    [DataRow("ValueTask", "both")]
    public void RuntimeAsyncGenericInstantiations(string returnType, string context)
    {
        const string hints = """
            [GenericArguments(typeof(int))]
            [GenericArguments(typeof(long))]
            [GenericArguments(typeof(string))]
            [GenericArguments(typeof(object))]
            """;
        bool genericType = context != "method";
        bool genericMethod = context != "type";
        var text = DisassembleSource($$"""
            using System;
            using System.Threading.Tasks;
            using System.Runtime.CompilerServices;
            using DotNetLab;
            {{(genericType ? hints : "")}}
            class Program{{(genericType ? "<T>" : "")}}
            {
                static Program() => throw new InvalidOperationException("User code must not execute.");
                {{(genericMethod ? hints : "")}}
                public static async {{returnType}} M{{(genericMethod ? "<U>" : "")}}()
                {
                    await Task.Yield();
                    {{(genericType ? MarkerCalls("T", "Outer") : "")}}
                    {{(genericMethod ? MarkerCalls("U", "Inner") : "")}}
                }
            }
            class Markers
            {
                [MethodImpl(MethodImplOptions.NoInlining)] public static void OuterInt(int tag) { }
                [MethodImpl(MethodImplOptions.NoInlining)] public static void OuterLong(int tag) { }
                [MethodImpl(MethodImplOptions.NoInlining)] public static void OuterReference(int tag) { }
                [MethodImpl(MethodImplOptions.NoInlining)] public static void InnerInt(int tag) { }
                [MethodImpl(MethodImplOptions.NoInlining)] public static void InnerLong(int tag) { }
                [MethodImpl(MethodImplOptions.NoInlining)] public static void InnerReference(int tag) { }
            }
            """, runtimeAsync: true);

        string[] types = ["Int32", "Int64", "String", "Object"];
        foreach (var type in genericType ? types : [""])
        foreach (var argument in genericMethod ? types : [""])
        {
            var declaringType = genericType ? $"Program`1[System.{type}]" : "Program";
            var method = genericMethod ? $"M<{argument}>()" : "M()";
            var body = AssertMethodBody(text, $"{declaringType}.{method}");
            if (genericType)
            {
                AssertMarker(body, "Outer", type);
            }
            if (genericMethod)
            {
                AssertMarker(body, "Inner", argument);
            }
        }

        static string MarkerCalls(string type, string prefix) => $$"""
            if (typeof({{type}}) == typeof(int)) Markers.{{prefix}}Int({{(prefix == "Outer" ? 0x1111 : 0x4444)}});
            else if (typeof({{type}}) == typeof(long)) Markers.{{prefix}}Long({{(prefix == "Outer" ? 0x2222 : 0x5555)}});
            else Markers.{{prefix}}Reference({{(prefix == "Outer" ? 0x3333 : 0x6666)}});
            """;

        static void AssertMarker(string body, string prefix, string type)
        {
            int expected = type switch { "Int32" => 0, "Int64" => 1, _ => 2 };
            int[] markers = prefix == "Outer" ? [0x1111, 0x2222, 0x3333] : [0x4444, 0x5555, 0x6666];
            for (int i = 0; i < markers.Length; i++)
            {
                // Argument constants identify bodies even when calls use unnamed runtime thunks.
                var pattern = $@"(?im)\bmov\s+\w+,\s*(?:0?{markers[i]:X}h|#(?:0x{markers[i]:x}|{markers[i]}))\s*$";
                bool present = Regex.IsMatch(body, pattern);
                Assert.AreEqual(i == expected, present, $"Incorrect {prefix} instantiation for {type}: marker 0x{markers[i]:x}.\n{body}");
            }
        }
    }

    private string DisassembleSource(string code, bool runtimeAsync = false, bool allowDisassemblyErrors = false)
    {
        var options = new CSharpParseOptions(LanguageVersion.Preview)
            .WithFeatures([new("runtime-async", runtimeAsync ? "on" : "off")]);
        var tree = CSharpSyntaxTree.ParseText(code, options);
        var references = RefAssemblies.All.Select(r => MetadataReference.CreateFromImage(r.Bytes))
            .Append(MetadataReference.CreateFromFile(typeof(GenericArgumentsAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create("Disassembly_" + Guid.NewGuid().ToString("N"), [tree], references,
            new(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        stream.Position = 0;
        var text = new JitAsmDisassembler().Disassemble(stream, []);
        TestContext.WriteLine(text);
        if (!allowDisassemblyErrors)
        {
            Assert.DoesNotContain("Exception:", text);
        }
        return text;
    }

    private static string AssertMethodBody(string asmText, string signature)
    {
        var match = Regex.Match(asmText,
            $@"(?m)^; {Regex.Escape(signature)}\r?\n(?<instructions>(?:(?!;)[^\r\n]*\r?\n)*)^; Total bytes of code (?<size>\d+)");
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
