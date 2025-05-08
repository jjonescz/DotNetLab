using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace DotNetLab.Lab;

internal sealed record CompilerProxyOptions
{
    public bool AssembliesAreAlwaysInDllFormat { get; set; }
}

/// <summary>
/// Can load our compiler project with any given Roslyn/Razor compiler version as dependency.
/// </summary>
internal sealed class CompilerProxy(
    IOptions<CompilerProxyOptions> options,
    ILogger<CompilerProxy> logger,
    DependencyRegistry dependencyRegistry,
    AssemblyDownloader assemblyDownloader,
    CompilerLoaderServices loaderServices,
    IServiceProvider serviceProvider)
{
    public static readonly string CompilerAssemblyName = "DotNetLab.Compiler";

    private LoadedCompiler? loaded;
    private int iteration;

    public async Task<CompiledAssembly> CompileAsync(CompilationInput input)
    {
        try
        {
            if (loaded is null || dependencyRegistry.Iteration != iteration)
            {
                var previousIteration = dependencyRegistry.Iteration;
                var currentlyLoaded = await LoadCompilerAsync();

                if (dependencyRegistry.Iteration == previousIteration)
                {
                    loaded = currentlyLoaded;
                    iteration = dependencyRegistry.Iteration;
                }
                else
                {
                    Debug.Assert(loaded is not null);
                }
            }

            if (input.Configuration is not null && loaded.DllAssemblies is null)
            {
                var assemblies = loaded.Assemblies ?? await LoadAssembliesAsync();
                loaded.DllAssemblies = assemblies.ToImmutableDictionary(p => p.Key, p => p.Value.GetDataAsDll());

                var builtInAssemblies = await LoadAssembliesAsync(builtIn: true);
                loaded.BuiltInDllAssemblies = builtInAssemblies.ToImmutableDictionary(p => p.Key, p => p.Value.GetDataAsDll());
            }

            using var _ = loaded.LoadContext.EnterContextualReflection();
            var result = loaded.Compiler.Compile(input, loaded.DllAssemblies, loaded.BuiltInDllAssemblies, loaded.LoadContext);

            if (loaded.LoadContext is CompilerLoader { LastFailure: { } failure })
            {
                loaded = null;
                throw new InvalidOperationException(
                    $"Failed to load '{failure.AssemblyName}'.", failure.Exception);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to compile.");
            return CompiledAssembly.Fail(ex.ToString());
        }
    }

    private async Task<ImmutableDictionary<string, LoadedAssembly>> LoadAssembliesAsync(bool builtIn = false)
    {
        var assemblies = ImmutableDictionary.CreateBuilder<string, LoadedAssembly>();

        if (!builtIn)
        {
            await foreach (var dep in dependencyRegistry.GetAssembliesAsync())
            {
                if (assemblies.ContainsKey(dep.Name))
                {
                    logger.LogWarning("Assembly already loaded from another dependency: {Name}", dep.Name);
                }

                assemblies[dep.Name] = dep;
            }
        }

        // All assemblies depending on Roslyn/Razor need to be reloaded
        // to avoid type mismatches between assemblies from different contexts.
        // If they are not loaded from the registry, we will reload the built-in ones.
        // We preload all built-in ones that our Compiler project depends on here
        // (we cannot do that inside the AssemblyLoadContext because of async).
        IEnumerable<string> names =
        [
            CompilerAssemblyName,
            ..CompilerInfo.Roslyn.AssemblyNames,
            ..CompilerInfo.Razor.AssemblyNames,
            "Microsoft.CodeAnalysis.CSharp.Test.Utilities",
            "Microsoft.CodeAnalysis.Razor.Test",
        ];
        foreach (var name in names)
        {
            if (!assemblies.ContainsKey(name))
            {
                var assembly = await LoadAssemblyAsync(name);
                assemblies.Add(name, assembly);
            }
        }

        logger.LogDebug("Available assemblies ({Count}): {Assemblies}",
            assemblies.Count,
            assemblies.Keys.JoinToString(", "));

        return assemblies.ToImmutableDictionary();
    }

    private async Task<LoadedAssembly> LoadAssemblyAsync(string name)
    {
        return new()
        {
            Name = name,
            Data = await assemblyDownloader.DownloadAsync(name),
            Format = options.Value.AssembliesAreAlwaysInDllFormat ? AssemblyDataFormat.Dll : AssemblyDataFormat.Webcil,
        };
    }

    private async Task<LoadedCompiler> LoadCompilerAsync()
    {
        ImmutableDictionary<string, LoadedAssembly>? assemblies = null;

        AssemblyLoadContext alc;
        if (dependencyRegistry.IsEmpty)
        {
            // Load the built-in compiler.
            alc = AssemblyLoadContext.Default;
        }
        else
        {
            assemblies ??= await LoadAssembliesAsync();
            alc = new CompilerLoader(loaderServices, assemblies, dependencyRegistry.Iteration);
        }

        using var _ = alc.EnterContextualReflection();
        Assembly compilerAssembly = alc.LoadFromAssemblyName(new(CompilerAssemblyName));
        Type compilerType = compilerAssembly.GetType(CompilerAssemblyName)!;
        var compiler = (ICompiler)ActivatorUtilities.CreateInstance(serviceProvider, compilerType)!;
        return new() { LoadContext = alc, Compiler = compiler, Assemblies = assemblies };
    }

    private sealed class LoadedCompiler
    {
        public required AssemblyLoadContext LoadContext { get; init; }
        public required ICompiler Compiler { get; init; }
        public required ImmutableDictionary<string, LoadedAssembly>? Assemblies { get; init; }
        public ImmutableDictionary<string, ImmutableArray<byte>>? DllAssemblies { get; set; }
        public ImmutableDictionary<string, ImmutableArray<byte>>? BuiltInDllAssemblies { get; set; }
    }
}

internal readonly record struct AssemblyLoadFailure
{
    public required AssemblyName AssemblyName { get; init; }
    public required Exception Exception { get; init; }
}

internal sealed record CompilerLoaderServices(
    ILogger<CompilerLoader> Logger);

internal sealed class CompilerLoader(
    CompilerLoaderServices services,
    IReadOnlyDictionary<string, LoadedAssembly> knownAssemblies,
    int iteration)
    : AssemblyLoadContext(nameof(CompilerLoader) + iteration)
{
    private readonly Dictionary<string, Assembly> loadedAssemblies = new();

    /// <summary>
    /// In production in WebAssembly, the loader exceptions aren't propagated to the caller.
    /// Hence this is used to fail the compilation when assembly loading fails.
    /// </summary>
    public AssemblyLoadFailure? LastFailure { get; set; }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        try
        {
            return LoadCore(assemblyName);
        }
        catch (Exception ex)
        {
            services.Logger.LogError(ex, "Failed to load {AssemblyName}.", assemblyName);
            LastFailure = new() { AssemblyName = assemblyName, Exception = ex };
            throw;
        }
    }

    private Assembly? LoadCore(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name)
        {
            if (loadedAssemblies.TryGetValue(name, out var loaded))
            {
                services.Logger.LogDebug("✔️ {AssemblyName}", assemblyName);

                return loaded;
            }

            if (knownAssemblies.TryGetValue(name, out var loadedAssembly))
            {
                services.Logger.LogDebug("▶️ {AssemblyName}", assemblyName);

                var bytes = ImmutableCollectionsMarshal.AsArray(loadedAssembly.Data)!;
                loaded = LoadFromStream(new MemoryStream(bytes));
                loadedAssemblies.Add(name, loaded);
                return loaded;
            }

            services.Logger.LogDebug("➖ {AssemblyName}", assemblyName);

            loaded = Default.LoadFromAssemblyName(assemblyName);
            loadedAssemblies.Add(name, loaded);
            return loaded;
        }

        return null;
    }
}
