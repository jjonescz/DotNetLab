using AsmArm64;
using Iced.Intel;
using System.Runtime.InteropServices;
using Decoder = Iced.Intel.Decoder;

namespace DotNetLab;

internal static class RuntimeEntryPoint
{
    public static ulong Resolve(MethodBase method, ulong entry, Action<ulong>? onEntryCell = null)
    {
        ulong context = method.IsGenericMethod
            ? (ulong)(nuint)method.MethodHandle.Value
            : (ulong)(nuint)(method.DeclaringType?.TypeHandle.Value ?? 0);
        bool generic = method.IsGenericMethod || method.DeclaringType?.IsGenericType == true;
        return Resolve(entry, context, generic, RuntimeInformation.ProcessArchitecture, onEntryCell);
    }

    internal static ulong Resolve(ulong entry, ulong context, bool generic, Architecture architecture, Action<ulong>? onEntryCell = null)
    {
        ulong target = ReadStub(entry, context: 0, architecture, onEntryCell) ?? entry;
        // Shared generics add an instantiation stub that loads the method/type context.
        if (generic && ReadStub(target, context, architecture) is { } shared)
        {
            target = ReadStub(shared, context: 0, architecture) ?? shared;
        }

        return target;
    }

    private static ulong? ReadStub(ulong entry, ulong context, Architecture architecture, Action<ulong>? onEntryCell = null)
    {
        var registers = new Dictionary<int, ulong>();
        bool foundContext = context == 0;
        ulong address = entry;
        for (int i = 0; i < 8; i++)
        {
            if (architecture is Architecture.X64 or Architecture.X86)
            {
                var decoder = Decoder.Create(architecture == Architecture.X64 ? 64 : 32, new NativeCodeReader(address), address);
                var instruction = decoder.Decode();
                if (instruction.Mnemonic == Mnemonic.Jmp)
                {
                    if (!foundContext)
                    {
                        return null;
                    }

                    if (instruction.Op0Kind == OpKind.Memory &&
                        (instruction.IsIPRelativeMemoryOperand ||
                         instruction.MemoryBase == Register.None && instruction.MemoryIndex == Register.None))
                    {
                        ulong cell = instruction.IsIPRelativeMemoryOperand
                            ? instruction.IPRelativeMemoryAddress : instruction.MemoryDisplacement64;
                        onEntryCell?.Invoke(cell);
                        return (ulong)(nuint)Marshal.ReadIntPtr((nint)cell);
                    }

                    return instruction.Op0Kind switch
                    {
                        OpKind.Register => registers.GetValueOrDefault((int)instruction.Op0Register) is var value && value != 0 ? value : null,
                        OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64 => instruction.NearBranchTarget,
                        _ => null,
                    };
                }

                if (context == 0 || instruction.Mnemonic != Mnemonic.Mov || instruction.Op0Kind != OpKind.Register)
                {
                    return null;
                }

                ulong immediate = instruction.Op1Kind switch
                {
                    OpKind.Immediate64 => instruction.Immediate64,
                    OpKind.Immediate32 => instruction.Immediate32,
                    OpKind.Register => registers.GetValueOrDefault((int)instruction.Op1Register),
                    OpKind.Memory when foundContext && instruction.MemoryIndex == Register.None &&
                        registers.TryGetValue((int)instruction.MemoryBase, out var cell) =>
                        (ulong)(nuint)Marshal.ReadIntPtr((nint)(cell + instruction.MemoryDisplacement64)),
                    _ => 0,
                };
                registers[(int)instruction.Op0Register] = immediate;
                foundContext |= immediate == context;
                address = instruction.NextIP;
            }
            else if (architecture == Architecture.Arm64)
            {
                var instruction = Arm64Instruction.Decode((uint)Marshal.ReadInt32((nint)address));
                if (instruction.Mnemonic == Arm64Mnemonic.BR)
                {
                    int register = ((Arm64RegisterOperand)instruction.GetOperand(0)).Value.Index;
                    return foundContext && registers.TryGetValue(register, out var value) ? value : null;
                }

                if (instruction.Mnemonic != Arm64Mnemonic.LDR)
                {
                    return null;
                }

                int destination = ((Arm64RegisterOperand)instruction.GetOperand(0)).Value.Index;
                var source = instruction.GetOperand(1);
                ulong immediate;
                if (source.Kind == Arm64OperandKind.Label)
                {
                    long offset = ((Arm64LabelOperand)source).Offset;
                    if (offset is < 0 or > 64)
                    {
                        return null;
                    }

                    immediate = (ulong)Marshal.ReadInt64((nint)(address + (ulong)offset));
                    if (context == 0)
                    {
                        onEntryCell?.Invoke(address + (ulong)offset);
                    }
                }
                else if (foundContext && source.Kind == Arm64OperandKind.Memory)
                {
                    var memory = ((Arm64MemoryOperand)source).Accessor;
                    if (memory.HasIndexRegister || !registers.TryGetValue(memory.BaseRegister.Index, out var cell))
                    {
                        return null;
                    }

                    immediate = (ulong)Marshal.ReadInt64((nint)(cell + (ulong)memory.Immediate));
                }
                else
                {
                    return null;
                }

                registers[destination] = immediate;
                foundContext |= immediate == context;
                address += 4;
            }
            else
            {
                return null;
            }
        }

        return null;
    }

    private sealed class NativeCodeReader(ulong address) : CodeReader
    {
        private int offset;
        public override int ReadByte() => offset < 15 ? Marshal.ReadByte((nint)address, offset++) : -1;
    }
}
