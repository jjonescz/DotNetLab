using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Iced.Intel;

namespace DotNetLab;

/// <remarks>
/// Inspired by <see href="https://github.com/compiler-explorer/dotnet-builder/blob/68da85ff965d2c784a0eaa0ce6f14f4d9692144c/build/DisassemblyLoader/Program.cs"/>.
/// </remarks>
internal sealed class JitAsmDisassembler : IJitAsmDisassembler
{
    public string Disassemble(MemoryStream emitStream, ImmutableArray<RefAssembly> references)
        => new Disassembly().Disassemble(emitStream, references);

    private sealed class Disassembly
    {
        /// <remarks> 
        /// Although this might look immutable, it is not (it shares string builders),
        /// hence it must not be reused across <see cref="JitAsmDisassembler.Disassemble"/> calls which might be concurrent.
        /// </remarks>
        private readonly Formatter formatter = new IntelFormatter(new FormatterOptions
        {
            HexSuffix = "h",
            OctalSuffix = "o",
            BinarySuffix = "b",
            FirstOperandCharIndex = 9,
            ShowSymbolAddress = true,
            SpaceAfterOperandSeparator = true,
            RipRelativeAddresses = true,
            SignedImmediateOperands = true,
            BranchLeadingZeros = false,
        }, new MonoSymbolResolver());

        private readonly HashSet<MethodBase> preparedMethods = new();
        private readonly HashSet<Type> preparedTypes = new();

        public string Disassemble(MemoryStream emitStream, ImmutableArray<RefAssembly> references)
        {
            var alc = new ExecutorLoader(references);
            try
            {
                var assembly = alc.LoadFromStream(emitStream);

                using var writer = new StringWriter();

                WriteMachineInfo(writer);

                foreach (var type in assembly.GetTypes())
                {
                    ProcessType(writer, type);
                }

                foreach (var attr in assembly.GetCustomAttributes<MethodInstantiationAttribute>())
                {
                    ProcessInstantiation(writer, assembly, containingType: null, attr);
                }

                return writer.ToString();
            }
            catch (Exception ex)
            {
                return ex.ToString();
            }
            finally
            {
                alc.Unload();
            }
        }

        private static void WriteMachineInfo(TextWriter writer)
        {
            writer.WriteLine($"; {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");
            writer.WriteLine();
        }

        private void ProcessType(TextWriter writer, Type type)
        {
            if (type.IsGenericTypeDefinition)
            {
                foreach (var attr in type.GetCustomAttributes<GenericArgumentsAttribute>())
                {
                    try
                    {
                        var genericType = type.MakeGenericType(attr.GenericArguments);
                        PrepareType(writer, genericType);
                        ProcessTypeInstantiations(writer, genericType);
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
            else
            {
                PrepareType(writer, type);
                ProcessTypeInstantiations(writer, type);
            }
        }

        private void PrepareType(TextWriter writer, Type type)
        {
            if (!preparedTypes.Add(type))
            {
                return;
            }

            try
            {
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            }
            catch (Exception ex)
            {
                writer.WriteLine($"; Failed to run class constructor for type {type}");
                foreach (var line in ex.ToString().AsSpan().EnumerateLines())
                {
                    writer.WriteLine($"; {line}");
                }
            }

            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                PrepareMethod(writer, constructor);
            }

            foreach (var method in type.GetTypeInfo().DeclaredMethods)
            {
                ProcessMethod(writer, method);
            }
        }

        private void ProcessMethod(TextWriter writer, MethodInfo method)
        {
            if (method.IsGenericMethodDefinition)
            {
                foreach (var attr in method.GetCustomAttributes<GenericArgumentsAttribute>())
                {
                    try
                    {
                        var genericMethod = method.MakeGenericMethod(attr.GenericArguments);
                        PrepareMethod(writer, genericMethod);
                        PrepareStateMachineType(writer, genericMethod);
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
            else
            {
                PrepareMethod(writer, method);
                PrepareStateMachineType(writer, method);
            }
        }

        private void ProcessTypeInstantiations(TextWriter writer, Type type)
        {
            var definition = type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;
            foreach (var attr in definition.GetCustomAttributes<MethodInstantiationAttribute>())
            {
                ProcessInstantiation(writer, type.Assembly, type, attr);
            }
        }

        private void ProcessInstantiation(TextWriter writer, Assembly assembly, Type? containingType, MethodInstantiationAttribute attr)
        {
            var type = attr.TypeName == null ? containingType : assembly.GetType(attr.TypeName);
            if (type == null)
            {
                return;
            }

            if (type.ContainsGenericParameters)
            {
                try
                {
                    type = type.GetGenericTypeDefinition().MakeGenericType(attr.GenericTypeArguments);
                }
                catch
                {
                    return;
                }
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == attr.MethodName))
            {
                try
                {
                    if (method.IsGenericMethodDefinition)
                    {
                        if (method.GetGenericArguments().Length != attr.GenericMethodArguments.Length)
                        {
                            continue;
                        }

                        var genericMethod = method.MakeGenericMethod(attr.GenericMethodArguments);
                        PrepareMethod(writer, genericMethod);
                        PrepareStateMachineType(writer, genericMethod);
                    }
                    else if (attr.GenericMethodArguments.Length == 0)
                    {
                        PrepareMethod(writer, method);
                        PrepareStateMachineType(writer, method);
                    }
                }
                catch
                {
                    continue;
                }
            }
        }

        private void PrepareStateMachineType(TextWriter writer, MethodInfo method)
        {
            var stateMachineType = method.GetCustomAttribute<StateMachineAttribute>()?.StateMachineType;
            if (stateMachineType == null)
            {
                return;
            }

            if (stateMachineType.ContainsGenericParameters)
            {
                var genericArguments = (method.DeclaringType?.GetGenericArguments() ?? Type.EmptyTypes)
                    .Concat(method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes)
                    .ToArray();
                if (genericArguments.Any(argument => argument.ContainsGenericParameters))
                {
                    return;
                }

                stateMachineType = stateMachineType.GetGenericTypeDefinition().MakeGenericType(genericArguments);
            }

            PrepareType(writer, stateMachineType);
        }

        private unsafe void PrepareMethod(TextWriter writer, MethodBase methodBase)
        {
            if (!preparedMethods.Add(methodBase))
            {
                return;
            }

            try
            {
                RuntimeHelpers.PrepareMethod(methodBase.MethodHandle);

                if (MonoJitInspector.TryGetJitCode(methodBase.MethodHandle, out var code, out var size, out var diagnostics))
                {
                    MonoJitInspector.WriteDisassembly(methodBase, (byte*)code, size, formatter, writer);
                }
                else
                {
                    writer.WriteLine($"; Failed to find code for method {methodBase}");
                    writer.WriteLine($"; {diagnostics}");
                    writer.WriteLine();
                }
            }
            catch (Exception ex)
            {
                writer.WriteLine($"; Failed to generate code for method {methodBase}");
                foreach (var line in ex.ToString().AsSpan().EnumerateLines())
                {
                    writer.WriteLine($"; {line}");
                }
                writer.WriteLine();
            }
        }
    }
}
