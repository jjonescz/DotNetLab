using System.Runtime.CompilerServices;
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

            using var writer = new StringWriter();
            writer.WriteLine($"; {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");
            using var provider = new RuntimeJitCodeProvider();
            var prepared = new List<(MethodBase Method, JitCode? Code, string? Comment, Exception? Error)>();
            foreach (var method in GetMethods(assembly, writer))
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
                    try
                    {
                        disassembler.AddMethod(method, code);
                    }
                    catch (Exception ex)
                    {
                        writer.WriteLine();
                        writer.WriteLine($"; Failed to discover symbols for '{JitCodeDisassembler.FormatMethod(method)}'.");
                        WriteError(writer, ex);
                    }
                }
            }

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

    private static IEnumerable<MethodBase> GetMethods(Assembly assembly, TextWriter writer)
    {
        var methods = new HashSet<MethodBase>();
        var types = new HashSet<Type>();
        foreach (var type in assembly.DefinedTypes)
        {
            AddType(type);
            if (type.IsGenericTypeDefinition)
            {
                foreach (var attribute in type.GetCustomAttributesData().Where(a => a.AttributeType == typeof(GenericArgumentsAttribute)))
                {
                    TryAdd($"type '{type}' from {nameof(GenericArgumentsAttribute)}",
                        () => AddType(type.MakeGenericType(ReadTypeArguments(attribute.ConstructorArguments[0]))));
                }
            }
        }

        foreach (var attribute in assembly.GetCustomAttributesData().Where(a => a.AttributeType == typeof(MethodInstantiationAttribute)))
        {
            TryAdd($"assembly-level {nameof(MethodInstantiationAttribute)}",
                () => AddInstantiation(null, attribute));
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
                TryAdd($"method '{method}'", () => AddMethod(method));
                if (method.IsGenericMethodDefinition && !type.ContainsGenericParameters)
                {
                    foreach (var attribute in method.GetCustomAttributesData().Where(a => a.AttributeType == typeof(GenericArgumentsAttribute)))
                    {
                        TryAdd($"method '{method}' from {nameof(GenericArgumentsAttribute)}",
                            () => AddMethod(method.MakeGenericMethod(ReadTypeArguments(attribute.ConstructorArguments[0]))));
                    }
                }
            }

            if (!type.ContainsGenericParameters)
            {
                foreach (var attribute in type.GetCustomAttributesData().Where(a => a.AttributeType == typeof(MethodInstantiationAttribute)))
                {
                    TryAdd($"type '{type}' from {nameof(MethodInstantiationAttribute)}",
                        () => AddInstantiation(type, attribute));
                }
            }
        }

        void AddMethod(MethodInfo method)
        {
            if (!methods.Add(method) || method.ContainsGenericParameters)
            {
                return;
            }

            foreach (var attribute in method.GetCustomAttributesData())
            {
                if (attribute.AttributeType != typeof(AsyncStateMachineAttribute) &&
                    attribute.AttributeType != typeof(IteratorStateMachineAttribute) &&
                    attribute.AttributeType != typeof(AsyncIteratorStateMachineAttribute))
                {
                    continue;
                }

                if (attribute.ConstructorArguments is not [{ Value: Type stateMachine }])
                {
                    throw new ArgumentException($"Invalid state-machine attribute on '{method}'.");
                }

                if (stateMachine.ContainsGenericParameters)
                {
                    stateMachine = stateMachine.MakeGenericType(
                        (method.DeclaringType?.GetGenericArguments() ?? [])
                        .Concat(method.IsGenericMethod ? method.GetGenericArguments() : []).ToArray());
                }

                AddType(stateMachine);
            }
        }

        void AddInstantiation(Type? containingType, CustomAttributeData attribute)
        {
            var arguments = attribute.ConstructorArguments;
            int offset = arguments.Count == 4 ? 1 : 0;
            var typeName = offset == 1 ? (string?)arguments[0].Value : null;
            var methodName = (string?)arguments[offset].Value;
            var genericTypeArguments = offset == 1 ? ReadTypeArguments(arguments[2]) : Type.EmptyTypes;
            var genericMethodArguments = ReadTypeArguments(arguments[^1]);
            var type = typeName is { } name
                ? assembly.GetType(name, throwOnError: true)!
                : containingType ?? throw new ArgumentException("An assembly-level method instantiation must specify a type name.");
            if (type.ContainsGenericParameters)
            {
                type = type.MakeGenericType(genericTypeArguments);
            }

            bool matched = false;
            foreach (var method in type.GetTypeInfo().DeclaredMethods.Where(m => m.Name == methodName))
            {
                if (method.IsGenericMethodDefinition && method.GetGenericArguments().Length == genericMethodArguments.Length)
                {
                    matched = true;
                    TryAdd($"overload '{method}' from {nameof(MethodInstantiationAttribute)}",
                        () => AddMethod(method.MakeGenericMethod(genericMethodArguments)));
                }
                else if (!method.IsGenericMethodDefinition && genericMethodArguments.Length == 0)
                {
                    matched = true;
                    TryAdd($"overload '{method}' from {nameof(MethodInstantiationAttribute)}", () => AddMethod(method));
                }
            }

            if (!matched)
            {
                throw new ArgumentException($"No matching overload of '{type}.{methodName}' accepts {genericMethodArguments.Length} generic method arguments.");
            }
        }

        static Type[] ReadTypeArguments(CustomAttributeTypedArgument argument)
        {
            if (argument.Value is null)
            {
                return [];
            }

            var values = (IReadOnlyCollection<CustomAttributeTypedArgument>)argument.Value;
            return argument.ArgumentType == typeof(string[])
                ? values.Select(value => Type.GetType((string)value.Value!)).OfType<Type>().ToArray()
                : values.Select(value => (Type)value.Value!).ToArray();
        }

        void TryAdd(string description, Action add)
        {
            try
            {
                add();
            }
            catch (Exception ex) when (ex is ArgumentException or TypeLoadException or FileNotFoundException or FileLoadException)
            {
                writer.WriteLine();
                writer.WriteLine($"; Cannot instantiate {description}.");
                WriteError(writer, ex);
            }
        }
    }
}
