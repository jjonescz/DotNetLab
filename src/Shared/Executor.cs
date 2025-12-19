using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace DotNetLab;

public static class Executor
{
    public static async Task<string> ExecuteAsync(MemoryStream emitStream, ImmutableArray<RefAssembly> assemblies)
    {
        var alc = new ExecutorLoader(assemblies);
        try
        {
            var assembly = alc.LoadFromStream(emitStream);

            var entryPoint = assembly.EntryPoint
                ?? throw new ArgumentException("No entry point found in the assembly.");

            int exitCode = 0;
            (string stdout, string stderr) = await Util.CaptureConsoleOutputAsync(
                async () =>
                {
                    try
                    {
                        exitCode = await InvokeEntryPointAsync(entryPoint);
                    }
                    catch (TargetInvocationException e)
                    {
                        Console.Error.WriteLine($"Unhandled exception. {e.InnerException ?? e}");
                        exitCode = unchecked((int)0xE0434352);
                    }
                });

            return $"Exit code: {exitCode}\nStdout:\n{stdout}\nStderr:\n{stderr}";
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

    public static async Task<int> InvokeEntryPointAsync(MethodInfo entryPoint)
    {
        (object? target, entryPoint) = getEntryPoint(entryPoint);
        var parameters = entryPoint.GetParameters().Length == 0
            ? null
            : new object[] { Array.Empty<string>() };
        var @return = entryPoint.Invoke(target, parameters);
        switch (@return)
        {
            case Task<int> taskInt:
                @return = await taskInt.ConfigureAwait(false);
                break;
            case Task<object> taskObject:
                @return = await taskObject.ConfigureAwait(false);
                break;
            case Task task:
                await task.ConfigureAwait(false);
                @return = 0;
                break;
            case ValueTask<int> valueTaskInt:
                @return = await valueTaskInt.ConfigureAwait(false);
                break;
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                @return = 0;
                break;
        }
        return @return is int e ? e : 0;

        // Obtains the async Main method if available because we cannot run the synchronous Main method
        // (because it uses Task.Wait which is unsupported in browser wasm).
        static (object? Target, MethodInfo Method) getEntryPoint(MethodInfo main)
        {
            try
            {
                var bytes = main.GetMethodBody()?.GetILAsByteArray();
                (object? Target, MethodBase? Method) invoke = bytes switch
                {
                    // Patterns for async Main method with arguments
                    [
                        (byte)ILOpCode.Ldarg_0,
                        (byte)ILOpCode.Call, _, _, _, _,
                        (byte)ILOpCode.Callvirt, _, _, _, _,
                        (byte)ILOpCode.Stloc_0,
                        (byte)ILOpCode.Ldloca_s, 0,
                        (byte)ILOpCode.Call, _, _, _, _,
                        (byte)ILOpCode.Ret
                    ] => (null, main.Module.ResolveMethod(BitConverter.ToInt32(bytes.AsSpan(2, 4)))),
                    // Patterns for async Main method without arguments
                    [
                        (byte)ILOpCode.Call, _, _, _, _,
                        (byte)ILOpCode.Callvirt, _, _, _, _,
                        (byte)ILOpCode.Stloc_0,
                        (byte)ILOpCode.Ldloca_s, 0,
                        (byte)ILOpCode.Call, _, _, _, _,
                        (byte)ILOpCode.Ret
                    ] => (null, main.Module.ResolveMethod(BitConverter.ToInt32(bytes.AsSpan(1, 4)))),
                    // Patterns for async Script Main method
                    [
                        (byte)ILOpCode.Newobj, _, _, _, _,
                        (byte)ILOpCode.Callvirt, _, _, _, _,
                        (byte)ILOpCode.Callvirt, _, _, _, _,
                        (byte)ILOpCode.Stloc_0,
                        (byte)ILOpCode.Ldloca_s, 0,
                        (byte)ILOpCode.Call, _, _, _, _,
                        (byte)ILOpCode.Pop,
                        (byte)ILOpCode.Ret
                    ] => CreateScriptMain(main, bytes),
                    _ => (null, null),
                };
                static (object? Target, MethodInfo? Method) CreateScriptMain(MethodInfo main, byte[] bytes)
                {
                    ConstructorInfo? constructor = main.Module.ResolveMethod(BitConverter.ToInt32(bytes.AsSpan(1, 4))) as ConstructorInfo;
                    MethodInfo? initialize = main.Module.ResolveMethod(BitConverter.ToInt32(bytes.AsSpan(6, 4))) as MethodInfo;
                    if (constructor == null || initialize == null) { return (null, null); }
                    object instance = constructor.Invoke(null);
                    return (instance, initialize);
                }
                if (invoke.Method is MethodInfo { ReturnType: Type type } info && (type == typeof(Task) || type.IsSubclassOf(typeof(Task))))
                {
                    return (invoke.Target, info);
                }
            }
            catch { }
            return (null, main);
        }
    }

    public static async Task<string> RenderComponentToHtmlAsync(MemoryStream emitStream, string componentTypeName)
    {
        var alc = new AssemblyLoadContext(nameof(RenderComponentToHtmlAsync), isCollectible: true);
        try
        {
            var assembly = alc.LoadFromStream(emitStream);
            var componentType = assembly.GetType(componentTypeName)
                ?? throw new InvalidOperationException($"Cannot find component '{componentTypeName}' in the assembly.");

            var services = new ServiceCollection();
            services.AddLogging();
            var serviceProvider = services.BuildServiceProvider();
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var renderer = new HtmlRenderer(serviceProvider, loggerFactory);
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync(componentType);
                return output.ToHtmlString();
            });
            return html;
        }
        finally
        {
            alc.Unload();
        }
    }
}

public sealed class ExecutorLoader(ImmutableArray<RefAssembly> assemblies) : AssemblyLoadContext(nameof(ExecutorLoader), isCollectible: true)
{
    private readonly Dictionary<string, Assembly> loadedAssemblies = new();
    private readonly IReadOnlyDictionary<string, ImmutableArray<byte>> lookup = assemblies
        .Where(a => a.LoadForExecution)
        .ToDictionary(a => a.Name, a => a.Bytes);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name)
        {
            if (loadedAssemblies.TryGetValue(name, out var loaded))
            {
                return loaded;
            }

            if (lookup.TryGetValue(name, out var bytes))
            {
                var array = ImmutableCollectionsMarshal.AsArray(bytes)!;
                loaded = LoadFromStream(new MemoryStream(array));
                loadedAssemblies.Add(name, loaded);
                return loaded;
            }

            loaded = Default.LoadFromAssemblyName(assemblyName);
            loadedAssemblies.Add(name, loaded);
            return loaded;
        }

        return null;
    }
}
