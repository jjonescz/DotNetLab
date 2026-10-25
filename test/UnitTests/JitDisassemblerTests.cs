using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
    public void SymbolDiscoveryFailuresPreservePreparedDisassembly()
    {
        var text = DisassembleSource("""
            using DotNetLab;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            [GenericArguments(typeof(string))]
            class G<T> where T : struct { }
            class Program
            {
                public static int FirstCaller() => InvalidHelper();
                [MethodImpl(MethodImplOptions.NoInlining)]
                private static int InvalidHelper() => 0;
                public static int SecondCaller() => InvalidHelper();
                public static int Healthy() => 42;
                [DllImport("not-loaded")]
                public static extern void PInvoke();
            }
            """, allowDisassemblyErrors: true, editAssembly: stream =>
            {
                using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                var metadata = pe.GetMetadataReader();
                var method = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition)
                    .Single(m => metadata.GetString(m.Name) == "InvalidHelper");
                int rva = method.RelativeVirtualAddress;
                var section = pe.PEHeaders.SectionHeaders[pe.PEHeaders.GetContainingSectionIndex(rva)];
                int offset = section.PointerToRawData + rva - section.VirtualAddress;
                var bytes = stream.GetBuffer();
                Assert.AreEqual(2, bytes[offset] & 3, "The test helper must have a tiny IL header.");
                // Removing the return value makes the non-inlined helper fail JIT compilation.
                bytes[offset + 1] = 0x00; // nop
            });

        Assert.Contains("; Cannot instantiate", text);
        Assert.Contains("; P/Invoke methods cannot be JIT-compiled.", text);
        Assert.Contains("; Failed to discover symbols for 'Program.FirstCaller()'.", text);
        Assert.Contains("; Failed to discover symbols for 'Program.SecondCaller()'.", text);
        Assert.Contains("; System.InvalidProgramException:", text);
        AssertMethodBody(text, "Program.FirstCaller()");
        AssertMethodBody(text, "Program.SecondCaller()");
        AssertMethodBody(text, "Program.Healthy()");
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
    [DataRow(0x7fff_ffffu)]
    [DataRow(0x8000_0000u)]
    [DataRow(0xffff_ffffu)]
    public void EntryPointPointerCellsPreserveUnsignedAddresses(uint target)
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X86 or Architecture.X64))
        {
            Assert.Inconclusive("This test exercises x86/x64 entry stubs.");
        }

        var memory = Marshal.AllocHGlobal(32);
        try
        {
            ulong entry = (ulong)(nuint)memory;
            ulong cell = (ulong)(nuint)(memory + 16);
            var bytes = new byte[32];
            bytes[0] = 0xff;
            bytes[1] = 0x25;
            BitConverter.GetBytes(IntPtr.Size == 4 ? (uint)cell : 10u).CopyTo(bytes, 2);
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            Marshal.WriteIntPtr(memory + 16, unchecked((nint)(nuint)target));

            ulong reportedCell = 0;
            var resolved = RuntimeEntryPoint.Resolve(entry, 0, generic: false,
                RuntimeInformation.ProcessArchitecture, address => reportedCell = address);

            Assert.AreEqual((ulong)target, resolved);
            Assert.AreEqual(cell, reportedCell);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [TestMethod]
    [DataRow(0x7fff_ffffu)]
    [DataRow(0x8000_0000u)]
    [DataRow(0xffff_ffffu)]
    public void SymbolPointerCellsPreserveUnsignedAddresses(uint target)
    {
        var memory = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            ulong cell = (ulong)(nuint)memory;
            Marshal.WriteIntPtr(memory, unchecked((nint)(nuint)target));
            var decoder = Iced.Intel.Decoder.Create(64,
                new Iced.Intel.ByteArrayCodeReader([0xff, 0x15, 0, 0, 0, 0]), cell - 6);
            var instruction = decoder.Decode();
            Iced.Intel.ISymbolResolver resolver = new JitCodeDisassembler(new() { [target] = "Target()" });

            bool found = resolver.TryGetSymbol(in instruction, 0, 0, cell, IntPtr.Size, out var symbol);

            Assert.IsTrue(found, $"Symbol not found for native pointer 0x{target:x}.");
            Assert.AreEqual(cell, symbol.Address);
            Assert.AreEqual("Target()", symbol.Text.Text.Text);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [TestMethod]
    [DataRow(Architecture.X64, "return")]
    [DataRow(Architecture.X64, "direct-tail-call")]
    [DataRow(Architecture.X64, "indirect-tail-call")]
    [DataRow(Architecture.X64, "precode-lookalike")]
    [DataRow(Architecture.X64, "fixup-precode")]
    [DataRow(Architecture.Arm64, "return")]
    [DataRow(Architecture.Arm64, "direct-tail-call")]
    [DataRow(Architecture.Arm64, "indirect-tail-call")]
    [DataRow(Architecture.Arm64, "precode-lookalike")]
    [DataRow(Architecture.Arm64, "fixup-precode")]
    public void SharedGenericEntryStubs(Architecture architecture, string body)
    {
        var memory = Marshal.AllocHGlobal(256);
        try
        {
            var bytes = new byte[256];
            if (architecture == Architecture.X64)
            {
                new byte[] { 0xff, 0x25, 10, 0, 0, 0 }.CopyTo(bytes, 0);
                new byte[] { 0x48, 0xb9 }.CopyTo(bytes, 32);
                BitConverter.GetBytes(0x1234UL).CopyTo(bytes, 34);
                new byte[] { 0x48, 0xb8 }.CopyTo(bytes, 42);
                BitConverter.GetBytes((ulong)(nuint)(memory + 80)).CopyTo(bytes, 44);
                new byte[] { 0x48, 0x8b, 0x00, 0xff, 0xe0 }.CopyTo(bytes, 52);
                BitConverter.GetBytes((ulong)(nuint)(memory + 32)).CopyTo(bytes, 16);
                switch (body)
                {
                    case "direct-tail-call":
                        bytes[100] = 0xe9; // jmp callee
                        BitConverter.GetBytes(192 - 105).CopyTo(bytes, 101);
                        break;
                    case "indirect-tail-call":
                    case "precode-lookalike":
                    case "fixup-precode":
                        new byte[] { 0xff, 0x25 }.CopyTo(bytes, 100);
                        BitConverter.GetBytes(144 - 106).CopyTo(bytes, 102);
                        if (body != "indirect-tail-call")
                        {
                            new byte[] { 0x4c, 0x8b, 0x15 }.CopyTo(bytes, 106);
                            BitConverter.GetBytes(152 - 113).CopyTo(bytes, 109);
                            new byte[] { 0xff, 0x25 }.CopyTo(bytes, 113);
                            BitConverter.GetBytes((body == "fixup-precode" ? 160 : 168) - 119).CopyTo(bytes, 115);
                        }
                        break;
                    default:
                        bytes[100] = 0xc3;
                        break;
                }
                bytes[192] = 0xc3;
            }
            else
            {
                BitConverter.GetBytes(0x58000050u).CopyTo(bytes, 0); // ldr x16, #8
                BitConverter.GetBytes(0xd61f0200u).CopyTo(bytes, 4); // br x16
                BitConverter.GetBytes((ulong)(nuint)(memory + 32)).CopyTo(bytes, 8);
                BitConverter.GetBytes(0x5800008bu).CopyTo(bytes, 32); // ldr x11, #16
                BitConverter.GetBytes(0x580000b0u).CopyTo(bytes, 36); // ldr x16, #20
                BitConverter.GetBytes(0xf9400210u).CopyTo(bytes, 40); // ldr x16, [x16]
                BitConverter.GetBytes(0xd61f0200u).CopyTo(bytes, 44); // br x16
                BitConverter.GetBytes(0x1234UL).CopyTo(bytes, 48);
                BitConverter.GetBytes((ulong)(nuint)(memory + 80)).CopyTo(bytes, 56);
                switch (body)
                {
                    case "direct-tail-call":
                        BitConverter.GetBytes(0x14000000u | (192 - 100) / 4).CopyTo(bytes, 100);
                        break;
                    case "indirect-tail-call":
                    case "precode-lookalike":
                    case "fixup-precode":
                        BitConverter.GetBytes(0x5800000bu | ((144 - 100) / 4) << 5).CopyTo(bytes, 100);
                        BitConverter.GetBytes(0xd61f0160u).CopyTo(bytes, 104); // br x11
                        if (body != "indirect-tail-call")
                        {
                            BitConverter.GetBytes(0xd50339bfu).CopyTo(bytes, 108); // dmb ishld
                            BitConverter.GetBytes(0x5800000cu | ((152 - 112) / 4) << 5).CopyTo(bytes, 112);
                            BitConverter.GetBytes(0x5800000bu | (((body == "fixup-precode" ? 160 : 168) - 116) / 4) << 5).CopyTo(bytes, 116);
                            BitConverter.GetBytes(0xd61f0160u).CopyTo(bytes, 120);
                        }
                        break;
                    default:
                        BitConverter.GetBytes(0xd65f03c0u).CopyTo(bytes, 100);
                        break;
                }
                BitConverter.GetBytes(0xd65f03c0u).CopyTo(bytes, 192); // ret
            }

            BitConverter.GetBytes((ulong)(nuint)(memory + 100)).CopyTo(bytes, 80);
            BitConverter.GetBytes((ulong)(nuint)(memory + 192)).CopyTo(bytes, 144);
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var cells = new List<ulong>();
            ulong expected = (ulong)(nuint)(memory + (body == "fixup-precode" ? 192 : 100));
            Assert.AreEqual(expected, RuntimeEntryPoint.Resolve((ulong)(nuint)memory, 0x1234, generic: true, architecture, cells.Add));
            Assert.AreEqual(body == "fixup-precode", cells.Contains((ulong)(nuint)(memory + 144)),
                "Only runtime precode cells should acquire the requested method's symbol.");
            Assert.AreEqual((ulong)(nuint)(memory + 32), RuntimeEntryPoint.Resolve((ulong)(nuint)memory, 0x5678, generic: true, architecture));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [TestMethod]
    public void SharedGenericTailCallsPreserveMethodIdentity()
    {
        var text = DisassembleSource("""
            using DotNetLab;
            using System.Runtime.CompilerServices;
            class Program
            {
                [MethodImpl(MethodImplOptions.NoInlining)]
                public static int Target() => 42;
                [GenericArguments(typeof(string))]
                [GenericArguments(typeof(object))]
                [MethodImpl(MethodImplOptions.NoInlining)]
                public static int TailCall<T>() { Target(); return Target(); }
            }
            """, editAssembly: stream =>
            {
                using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                var metadata = pe.GetMetadataReader();
                var method = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition)
                    .Single(m => metadata.GetString(m.Name) == "TailCall");
                int rva = method.RelativeVirtualAddress;
                var section = pe.PEHeaders.SectionHeaders[pe.PEHeaders.GetContainingSectionIndex(rva)];
                int offset = section.PointerToRawData + rva - section.VirtualAddress;
                var bytes = stream.GetBuffer();
                Assert.AreEqual(2, bytes[offset] & 3, "The test method must have a tiny IL header.");
                Assert.AreEqual(0x28, (int)bytes[offset + 1], "The method must start with an IL call.");
                Assert.IsGreaterThanOrEqualTo(8, bytes[offset] >> 2);
                int token = BitConverter.ToInt32(bytes, offset + 2);
                bytes[offset] = (8 << 2) | 2;
                // An explicit tail. call; ret avoids depending on optional tail-call optimization.
                new byte[] { 0xfe, 0x14, 0x28 }.CopyTo(bytes, offset + 1);
                BitConverter.GetBytes(token).CopyTo(bytes, offset + 4);
                bytes[offset + 8] = 0x2a;
            });

        var target = AssertMethodBody(text, "Program.Target()");
        Assert.Contains("ret", target);
        foreach (var argument in new[] { "String", "Object" })
        {
            var body = AssertMethodBody(text, $"Program.TailCall<{argument}>()");
            Assert.IsTrue(Regex.IsMatch(body, @"(?m)^\s+(?:jmp|b|br)\s+"), $"Missing native tail jump.\n{body}");
            Assert.Contains("Program.Target()", body);
            Assert.DoesNotContain("ret", body);
            Assert.AreNotEqual(target, body);
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

    private string DisassembleSource(string code, bool runtimeAsync = false, bool allowDisassemblyErrors = false,
        Action<MemoryStream>? editAssembly = null)
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
        editAssembly?.Invoke(stream);
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
