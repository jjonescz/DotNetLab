using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace DotNetLab;

public static class Executor
{
    public static string Execute(MemoryStream emitStream, ImmutableArray<RefAssembly> assemblies)
    {
        var alc = new ExecutorLoader(assemblies);
        try
        {
            var assembly = alc.LoadFromStream(emitStream);

            var entryPoint = assembly.EntryPoint
                ?? throw new ArgumentException("No entry point found in the assembly.");

            int exitCode = 0;
            Util.CaptureConsoleOutput(
                () =>
                {
                    try
                    {
                        exitCode = InvokeEntryPoint(entryPoint);
                    }
                    catch (TargetInvocationException e)
                    {
                        Console.Error.WriteLine($"Unhandled exception. {e.InnerException ?? e}");
                        exitCode = unchecked((int)0xE0434352);
                    }
                },
                out string stdout, out string stderr);

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

    public static int InvokeEntryPoint(MethodInfo entryPoint)
    {
        var parameters = entryPoint.GetParameters().Length == 0
            ? null
            : new object[] { Array.Empty<string>() };
        return entryPoint.Invoke(null, parameters) is int e ? e : 0;
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

internal sealed class ExecutorLoader(ImmutableArray<RefAssembly> assemblies) : AssemblyLoadContext(nameof(ExecutorLoader), isCollectible: true)
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
