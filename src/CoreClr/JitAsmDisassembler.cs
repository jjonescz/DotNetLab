using System.Runtime.InteropServices;

namespace DotNetLab;

public sealed class JitAsmDisassembler : IJitAsmDisassembler
{
    private static readonly Lock syncRoot = new();

    public string Disassemble(MemoryStream emitStream, ImmutableArray<RefAssembly> references)
    {
        // Runtime EventListener reconfiguration is process-wide.
        lock (syncRoot)
        {
            return DisassembleCore(emitStream, references);
        }
    }

    private static string DisassembleCore(MemoryStream emitStream, ImmutableArray<RefAssembly> references)
    {
        var alc = new ExecutorLoader(references);
        try
        {
            var assembly = alc.LoadFromStream(emitStream);

            using var provider = new RuntimeJitCodeProvider();
            var prepared = new List<(MethodBase Method, JitCode? Code, string? Comment, Exception? Error)>();
            foreach (var method in GetMethods(assembly))
            {
                var comment = method switch
                {
                    { ContainsGenericParameters: true } => "Open generics cannot be JIT-compiled.",
                    { IsAbstract: true } => "Abstract methods cannot be JIT-compiled.",
                    _ when method.Attributes.HasFlag(MethodAttributes.PinvokeImpl) => "P/Invoke methods cannot be JIT-compiled.",
                    _ when method.MethodImplementationFlags.HasFlag(MethodImplAttributes.Runtime) => "Runtime-implemented methods cannot be JIT-compiled.",
                    _ when method.GetMethodBody() is null => "Extern methods cannot be JIT-compiled.",
                    _ => null,
                };
                try
                {
                    prepared.Add((method, comment is null ? provider.GetCode(method) : null, comment, null));
                }
                catch (Exception ex)
                {
                    prepared.Add((method, null, null, ex));
                }
            }

            var disassembler = new JitCodeDisassembler(provider.GetSymbols());
            foreach (var (method, code, _, _) in prepared)
            {
                if (code is not null)
                {
                    disassembler.AddMethod(method, code);
                }
            }

            using var writer = new StringWriter();
            writer.WriteLine($"; {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");
            foreach (var (method, code, comment, error) in prepared)
            {
                writer.WriteLine();
                writer.WriteLine($"; {JitCodeDisassembler.FormatMethod(method)}");
                try
                {
                    if (error is not null)
                    {
                        WriteError(writer, error);
                    }
                    else if (comment is not null)
                    {
                        writer.WriteLine($"; {comment}");
                    }
                    else
                    {
                        disassembler.Write(writer, code!);
                    }
                }
                catch (Exception ex)
                {
                    WriteError(writer, ex);
                }
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

    private static void WriteError(TextWriter writer, Exception error)
    {
        foreach (var line in error.ToString().AsSpan().EnumerateLines())
        {
            writer.WriteLine($"; {line}");
        }
    }

    private static IEnumerable<MethodBase> GetMethods(Assembly assembly)
    {
        var methods = new HashSet<MethodBase>();
        var types = new HashSet<Type>();
        foreach (var type in assembly.DefinedTypes)
        {
            AddType(type);
            if (type.IsGenericTypeDefinition)
            {
                foreach (var attribute in type.GetCustomAttributes<GenericArgumentsAttribute>())
                {
                    AddType(type.MakeGenericType(attribute.GenericArguments));
                }
            }
        }

        foreach (var attribute in assembly.GetCustomAttributes<MethodInstantiationAttribute>())
        {
            AddInstantiation(null, attribute);
        }

        return methods;

        void AddType(Type type)
        {
            if (!types.Add(type))
            {
                return;
            }

            foreach (var constructor in type.GetTypeInfo().DeclaredConstructors)
            {
                methods.Add(constructor);
            }

            foreach (var method in type.GetTypeInfo().DeclaredMethods)
            {
                AddMethod(method);
                if (method.IsGenericMethodDefinition && !type.ContainsGenericParameters)
                {
                    foreach (var attribute in method.GetCustomAttributes<GenericArgumentsAttribute>())
                    {
                        AddMethod(method.MakeGenericMethod(attribute.GenericArguments));
                    }
                }
            }

            if (!type.ContainsGenericParameters)
            {
                foreach (var attribute in type.GetCustomAttributes<MethodInstantiationAttribute>())
                {
                    AddInstantiation(type, attribute);
                }
            }
        }

        void AddMethod(MethodInfo method)
        {
            if (!methods.Add(method) || method.ContainsGenericParameters)
            {
                return;
            }

            var stateMachine = method.GetCustomAttribute<System.Runtime.CompilerServices.StateMachineAttribute>()?.StateMachineType;
            if (stateMachine is not null)
            {
                if (stateMachine.ContainsGenericParameters)
                {
                    stateMachine = stateMachine.MakeGenericType(
                        (method.DeclaringType?.GetGenericArguments() ?? [])
                        .Concat(method.IsGenericMethod ? method.GetGenericArguments() : []).ToArray());
                }

                AddType(stateMachine);
            }
        }

        void AddInstantiation(Type? containingType, MethodInstantiationAttribute attribute)
        {
            var type = attribute.TypeName is { } name
                ? assembly.GetType(name, throwOnError: true)!
                : containingType ?? throw new ArgumentException("An assembly-level method instantiation must specify a type name.");
            if (type.ContainsGenericParameters)
            {
                type = type.MakeGenericType(attribute.GenericTypeArguments);
            }

            foreach (var method in type.GetTypeInfo().DeclaredMethods.Where(m => m.Name == attribute.MethodName))
            {
                if (method.IsGenericMethodDefinition && method.GetGenericArguments().Length == attribute.GenericMethodArguments.Length)
                {
                    AddMethod(method.MakeGenericMethod(attribute.GenericMethodArguments));
                }
                else if (!method.IsGenericMethodDefinition && attribute.GenericMethodArguments.Length == 0)
                {
                    AddMethod(method);
                }
            }
        }
    }
}
