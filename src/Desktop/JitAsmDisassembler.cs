using JitInspect;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotNetLab;

internal sealed class JitAsmDisassembler : IJitAsmDisassembler
{
    public string Disassemble(MemoryStream emitStream, ImmutableArray<RefAssembly> references)
    {
        var alc = new ExecutorLoader(references);
        try
        {
            var assembly = alc.LoadFromStream(emitStream);

            var runtimeAsyncMethods = RuntimeAsyncMethodResolver.GetMethods(assembly);
            using var jitDisassembler = JitDisassembler.Create();
            using var asyncMethods = RuntimeAsyncMethodResolver.Create(jitDisassembler, runtimeAsyncMethods);

            var writer = new ArrayBufferWriter<char>();

            WriteMachineInfo(writer);

            foreach (var type in assembly.DefinedTypes)
            {
                var methods = type.DeclaredConstructors
                    .AsEnumerable<MethodBase>()
                    .Concat(type.DeclaredMethods);

                foreach (var method in methods)
                {
                    if (writer.WrittenCount != 0)
                    {
                        writer.Write(Environment.NewLine);
                    }

                    if (method.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
                    {
                        WriteSignatureFromReflection(jitDisassembler, writer, method);
                        writer.Write("; P/Invoke methods cannot be JIT-compiled.");
                        writer.Write(Environment.NewLine);
                        continue;
                    }

                    if (method.MethodImplementationFlags.HasFlag(MethodImplAttributes.Runtime))
                    {
                        WriteSignatureFromReflection(jitDisassembler, writer, method);
                        writer.Write("; Runtime-implemented methods cannot be JIT-compiled.");
                        writer.Write(Environment.NewLine);
                        continue;
                    }

                    if (method.GetMethodBody() is null)
                    {
                        WriteSignatureFromReflection(jitDisassembler, writer, method);
                        writer.Write("; Extern methods cannot be JIT-compiled.");
                        writer.Write(Environment.NewLine);
                        continue;
                    }

                    try
                    {
                        jitDisassembler.Disassemble(writer, asyncMethods?.Resolve(method) ?? method);
                    }
                    catch (Exception ex)
                    {
                        WriteSignatureFromReflection(jitDisassembler, writer, method);

                        foreach (var line in ex.ToString().EnumerateLines())
                        {
                            writer.Write($"; {line}");
                            writer.Write(Environment.NewLine);
                        }
                    }
                }
            }

            return writer.WrittenSpan.ToString();
        }
        catch (Exception ex)
        {
            return ex.ToString();
        }
        finally
        {
            alc.Unload();
        }

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(WriteSignatureFromReflection))]
        static extern void WriteSignatureFromReflection(JitDisassembler @this, IBufferWriter<char> writer, MethodBase method);
    }

    private static void WriteMachineInfo(IBufferWriter<char> writer)
    {
        writer.Write($"; {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");
        writer.Write(Environment.NewLine);
    }
}
