// From https://github.com/compiler-explorer/dotnet-builder/blob/68da85ff965d2c784a0eaa0ce6f14f4d9692144c/build/DisassemblyLoader/MonoJitInspector.cs.

using System.Runtime.InteropServices;
using Iced.Intel;
using static DotNetLab.MonoInterop;
using Decoder = Iced.Intel.Decoder;

namespace DotNetLab;

internal static class MonoJitInspector
{
    public static unsafe bool TryGetJitCode(RuntimeMethodHandle handle, out void* code, out int size, out string diagnostics)
    {
        // handle.GetFunctionPointer() returns a trampoline/precode address that is
        // not registered in the JIT-info table, so mono_jit_info_table_find can't
        // locate it. RuntimeMethodHandle.Value is the underlying MonoMethod*, which
        // mono_compile_method compiles and returns the real native code entry for.
        var monoMethod = (void*)handle.Value;
        var nativeCode = mono_compile_method(monoMethod);
        var fp = (void*)handle.GetFunctionPointer();

        var domain = mono_domain_get();
        var ji = mono_jit_info_table_find(domain, nativeCode);

        // Fall back to the function pointer in case mono_compile_method returned a
        // wrapper (e.g. an ftnptr) whose address isn't the code start.
        void* fpJi = null;
        if (ji == null)
        {
            fpJi = mono_jit_info_table_find(domain, fp);
            ji = fpJi;
        }

        diagnostics = $"MonoMethod=0x{(nuint)monoMethod:x} compiled=0x{(nuint)nativeCode:x} " +
            $"fp=0x{(nuint)fp:x} domain=0x{(nuint)domain:x} " +
            $"ji(compiled)=0x{(nuint)mono_jit_info_table_find(domain, nativeCode):x} " +
            $"ji(fp)=0x{(nuint)fpJi:x}";

        if (ji == null)
        {
            code = null;
            size = 0;
            return false;
        }

        code = mono_jit_info_get_code_start(ji);
        size = mono_jit_info_get_code_size(ji);
        return true;
    }

    private static void WriteComment(TextWriter writer, string comment)
    {
        writer.Write("; ");
        writer.WriteLine(comment);
    }

    public static unsafe void WriteDisassembly(MethodBase method, byte* code, int size, Formatter formatter, TextWriter writer)
    {
        var reader = new UnmanagedCodeReader(code, size);
        var decoder = Decoder.Create(8 * IntPtr.Size, reader);
        var output = new StringOutput();
        decoder.IP = (ulong)code;
        ulong tail = (ulong)(code + size);
        var methodName = FormatMethodName(method);

        WriteComment(writer, $"Assembly listing for method {methodName}");

        while (decoder.IP < tail)
        {
            var instr = decoder.Decode();
            formatter.Format(instr, output);
            writer.Write("       ");
            writer.WriteLine(output.ToStringAndReset());
        }

        WriteComment(writer, $"Total bytes of code {size} for method {methodName}");
        writer.WriteLine();
    }

    private static string FormatMethodName(MethodBase method)
    {
        var sb = new StringBuilder();
        if (method.DeclaringType is Type type)
        {
            sb.Append(type);
            sb.Append(':');
        }
        sb.Append(method.Name);
        sb.Append('(');
        sb.Append(string.Join(',', method.GetParameters().Select(p => p.ParameterType)));
        sb.Append(')');
        if (method is MethodInfo { ReturnType: var retType } && retType != typeof(void))
        {
            sb.Append(':');
            sb.Append(retType);
        }
        if (method.CallingConvention.HasFlag(CallingConventions.HasThis)
            && !method.CallingConvention.HasFlag(CallingConventions.ExplicitThis))
        {
            sb.Append(":this");
        }

        return sb.ToString();
    }
}

internal sealed class UnmanagedCodeReader : CodeReader
{
    public int Length { get; }

    public int Offset { get; private set; }

    public byte* Pointer { get; }

    public UnmanagedCodeReader(byte* pointer, int length)
    {
        Pointer = pointer;
        Length = length;
    }

    public override unsafe int ReadByte()
    {
        if (Offset >= Length)
            return -1;

        return Pointer[Offset++];
    }
}

internal sealed class MonoSymbolResolver : ISymbolResolver
{
    public unsafe bool TryGetSymbol(in Instruction instruction, int operand, int instructionOperand, ulong address, int addressSize, out SymbolResult symbol)
    {
        var name = mono_pmip((void*)address);
        if (name == null)
        {
            symbol = default;
            return false;
        }

        symbol = new SymbolResult(address, Marshal.PtrToStringAnsi((nint)name)?.Trim() ?? "unknown");
        return true;
    }
}
