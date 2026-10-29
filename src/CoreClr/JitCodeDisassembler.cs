using AsmArm64;
using Iced.Intel;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Decoder = Iced.Intel.Decoder;
using FlowControl = Iced.Intel.FlowControl;

namespace DotNetLab;

internal sealed class JitCodeDisassembler(Dictionary<ulong, string> symbols) : ISymbolResolver
{
    private static readonly Dictionary<short, OpCode> opCodes = typeof(OpCodes).GetFields()
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(op => op.Value);

    private readonly Dictionary<ulong, string> labels = new();

    public void AddMethod(MethodBase method, JitCode code)
    {
        var name = FormatMethod(method);
        symbols[code.Address] = name;
        AddEntryPoint(method);

        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            return;
        }

        for (int offset = 0; offset < il.Length;)
        {
            short value = il[offset++];
            if (value == 0xfe)
            {
                value = (short)(0xfe00 | il[offset++]);
            }

            var op = opCodes[value];
            if (op.OperandType == OperandType.InlineMethod)
            {
                var referenced = method.Module.ResolveMethod(BitConverter.ToInt32(il, offset),
                    method.DeclaringType?.GetGenericArguments(),
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (referenced is { ContainsGenericParameters: false, IsAbstract: false })
                {
                    AddEntryPoint(referenced);
                }
            }

            offset += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
                _ => 4,
            };
        }
    }

    private void AddEntryPoint(MethodBase method)
    {
        var name = FormatMethod(method);
        ulong entry = (ulong)(nuint)method.MethodHandle.GetFunctionPointer();
        symbols[entry] = name;
        if (method.GetMethodBody() is null)
        {
            return;
        }

        // Preparing patches the entry stub's cell; otherwise it can point at the shared prestub.
        RuntimeHelpers.PrepareMethod(method.MethodHandle);
        symbols[RuntimeEntryPoint.Resolve(method, entry, cell => symbols[cell] = name)] = name;
    }

    public void Write(TextWriter writer, JitCode code)
    {
        var bytes = new byte[code.Size];
        Marshal.Copy((nint)code.Address, bytes, 0, bytes.Length);
        Write(writer, code.Address, bytes, RuntimeInformation.ProcessArchitecture);
    }

    internal void Write(TextWriter writer, ulong address, byte[] bytes, Architecture architecture)
    {
        switch (architecture)
        {
            case Architecture.X86:
            case Architecture.X64:
                WriteIntel(writer, address, bytes, architecture == Architecture.X64 ? 64 : 32);
                break;
            case Architecture.Arm64:
                var disassembler = new Arm64Disassembler(new()
                {
                    BaseAddress = checked((long)address),
                    IndentSize = 7,
                    CommentPrefix = ";",
                    PrintLabelBeforeFirstInstruction = false,
                    TryFormatLabel = TryFormatArm64Symbol,
                });
                disassembler.Disassemble(bytes, writer);
                break;
            default:
                throw new PlatformNotSupportedException($"JIT disassembly is not supported on {architecture}.");
        }

        writer.WriteLine($"; Total bytes of code {bytes.Length}");
    }

    private bool TryFormatArm64Symbol(long address, Span<char> destination, out int charsWritten)
    {
        if (symbols.TryGetValue((ulong)address, out var name) && name.AsSpan().TryCopyTo(destination))
        {
            charsWritten = name.Length;
            return true;
        }

        charsWritten = 0;
        return false;
    }

    private void WriteIntel(TextWriter writer, ulong address, byte[] bytes, int bitness)
    {
        var decoder = Decoder.Create(bitness, new ByteArrayCodeReader(bytes), address);
        var instructions = new List<Instruction>();
        while (decoder.IP < address + (uint)bytes.Length)
        {
            instructions.Add(decoder.Decode());
        }

        labels.Clear();
        var targets = instructions
            .Where(i => i.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
            .Select(i => i.NearBranchTarget).ToHashSet();
        foreach (var instruction in instructions)
        {
            if (targets.Contains(instruction.IP))
            {
                labels[instruction.IP] = $"L{labels.Count:00}";
            }
        }

        // Formatters share mutable buffers, so create one per disassembly, as on the old Mono backend.
        var formatter = new IntelFormatter(new FormatterOptions
        {
            HexSuffix = "h",
            OctalSuffix = "o",
            BinarySuffix = "b",
            FirstOperandCharIndex = 9,
            ShowSymbolAddress = false,
            SpaceAfterOperandSeparator = true,
            RipRelativeAddresses = true,
            SignedImmediateOperands = true,
            BranchLeadingZeros = false,
        }, this);
        var output = new StringOutput();
        foreach (var instruction in instructions)
        {
            if (labels.TryGetValue(instruction.IP, out var label))
            {
                writer.WriteLine($"{label}:");
            }

            formatter.Format(instruction, output);
            writer.WriteLine($"       {output.ToStringAndReset()}");
        }
    }

    bool ISymbolResolver.TryGetSymbol(in Instruction instruction, int operand, int instructionOperand,
        ulong address, int addressSize, out SymbolResult symbol)
    {
        if (labels.TryGetValue(address, out var name) || symbols.TryGetValue(address, out name))
        {
            symbol = new(address, name);
            return true;
        }

        // CoreCLR's RIP-relative indirect calls use runtime-owned entry-point cells.
        if (instruction.IsIPRelativeMemoryOperand && address == instruction.IPRelativeMemoryAddress &&
            instruction.FlowControl is FlowControl.IndirectCall or FlowControl.IndirectBranch &&
            symbols.TryGetValue((ulong)(nuint)Marshal.ReadIntPtr((nint)address), out name))
        {
            symbol = new(address, name);
            return true;
        }

        symbol = default;
        return false;
    }

    public static string FormatMethod(MethodBase method) =>
        $"{method.DeclaringType}.{method.Name}" +
        (method.IsGenericMethod ? $"<{string.Join(", ", method.GetGenericArguments().Select(t => t.Name))}>" : "") +
        $"({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";
}
