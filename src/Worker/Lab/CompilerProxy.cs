﻿using Microsoft.Extensions.DependencyInjection;
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

    private readonly Dictionary<string, LoadedAssembly> builtInAssemblyCache = [];
    private LoadedCompiler? loaded;
    private int iteration;

    private async Task EnsureLoadedAsync()
    {
        if (loaded is null || dependencyRegistry.Iteration != iteration)
        {
            var previousIteration = dependencyRegistry.Iteration;
            var currentlyLoaded = await LoadCompilerAsync();

            if (dependencyRegistry.Iteration == previousIteration)
            {
                loaded?.Dispose();
                loaded = currentlyLoaded;
                iteration = dependencyRegistry.Iteration;
            }
            else
            {
                Debug.Assert(loaded is not null);
            }
        }
    }

    public async Task<CompiledAssembly> CompileAsync(CompilationInput input)
    {
        try
        {
            await EnsureLoadedAsync();
            Debug.Assert(loaded is not null);

            if (input.Configuration is not null && loaded.DllAssemblies is null)
            {
                var assemblies = loaded.Assemblies ?? await LoadAssembliesAsync();
                loaded.DllAssemblies = assemblies.ToImmutableDictionary(p => p.Key, p => p.Value.DataAsDll);

                var builtInAssemblies = await LoadAssembliesAsync(builtInOnly: true);
                loaded.BuiltInDllAssemblies = builtInAssemblies.ToImmutableDictionary(p => p.Key, p => p.Value.DataAsDll);
            }

            CompiledAssembly result;
            using (loaded.LoadContext.EnterContextualReflection())
            {
                result = await loaded.Compiler.CompileAsync(input, loaded.DllAssemblies, loaded.BuiltInDllAssemblies, loaded.LoadContext);
            }

            if (loaded.LoadContext is CompilerLoader { LastFailure: { } failure })
            {
                loaded?.Dispose();
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

    public async Task<ILanguageServices> GetLanguageServicesAsync()
    {
        await EnsureLoadedAsync();
        Debug.Assert(loaded is not null);
        return loaded.LanguageServices.Value;
    }

    private async Task<ImmutableDictionary<string, LoadedAssembly>> LoadAssembliesAsync(bool builtInOnly = false)
    {
        var assemblies = ImmutableDictionary.CreateBuilder<string, LoadedAssembly>();

        if (!builtInOnly)
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
            "Microsoft.CodeAnalysis.VisualBasic",
            "Microsoft.CodeAnalysis.Workspaces",
            "Microsoft.CodeAnalysis.CSharp.Workspaces",
            "Microsoft.CodeAnalysis.VisualBasic.Workspaces",
            "Microsoft.CodeAnalysis.Features",
            "Microsoft.CodeAnalysis.CSharp.Features",
            "Microsoft.CodeAnalysis.VisualBasic.Features",
            "Microsoft.CodeAnalysis.CodeStyle",
            "Microsoft.CodeAnalysis.CSharp.CodeStyle",
            "Microsoft.CodeAnalysis.CSharp.Test.Utilities", // RoslynAccess project produces this assembly
            "Microsoft.CodeAnalysis.Razor.Test", // RazorAccess project produces this assembly
            "Microsoft.CodeAnalysis.CSharp.CodeStyle.UnitTests", // RoslynCodeStyleAccess project produces this assembly
            "Microsoft.CodeAnalysis.Workspaces.UnitTests", // RoslynWorkspaceAccess project produces this assembly
        ];
        foreach (var name in names)
        {
            if (!assemblies.ContainsKey(name))
            {
                if (!builtInAssemblyCache.TryGetValue(name, out var assembly))
                {
                    assembly = await LoadAssemblyAsync(name);
                    assembly = builtInAssemblyCache.GetOrAdd(name, assembly);
                }

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
            assemblies = await LoadAssembliesAsync();
            alc = new CompilerLoader(loaderServices, assemblies, dependencyRegistry.Iteration);
        }

        using var _ = alc.EnterContextualReflection();
        Assembly compilerAssembly = alc.LoadFromAssemblyName(new(CompilerAssemblyName));
        Type compilerType = compilerAssembly.GetType("DotNetLab.Compiler")!;
        var compiler = (ICompiler)ActivatorUtilities.CreateInstance(serviceProvider, compilerType)!;
        var languageServices = new Lazy<ILanguageServices>(() =>
        {
            var languageServicesType = compilerAssembly.GetType("DotNetLab.LanguageServices")!;
            return (ILanguageServices)ActivatorUtilities.CreateInstance(serviceProvider, languageServicesType, [compiler])!;
        });
        return new() { LoadContext = alc, Compiler = compiler, LanguageServices = languageServices, Assemblies = assemblies };
    }

    private sealed class LoadedCompiler : IDisposable
    {
        public required AssemblyLoadContext LoadContext { get; init; }
        public required ICompiler Compiler { get; init; }
        public required Lazy<ILanguageServices> LanguageServices { get; init; }

        /// <summary>
        /// If a custom (not the built-in) compiler version is required, its assemblies will be loaded here.
        /// </summary>
        public required ImmutableDictionary<string, LoadedAssembly>? Assemblies { get; init; }

        /// <summary>
        /// If Configuration is provided, the compiler needs Roslyn DLLs to compile the Configuration against.
        /// This property is used to cache the loaded DLL bytes.
        /// It is computed from <see cref="Assemblies"/> (if a custom compiler is used)
        /// or from <see cref="LoadAssembliesAsync"/> (if the built-in compiler is used).
        /// </summary>
        public ImmutableDictionary<string, ImmutableArray<byte>>? DllAssemblies { get; set; }

        /// <summary>
        /// Similar to <see cref="DllAssemblies"/>, but always contains the built-in Roslyn DLLs.
        /// These are used if the user specifies a custom compiler version that is older than the built-in one
        /// (in which case using the custom DLLs would fail). Currently, we don't check the compiler version,
        /// we just try these built-in ones when compilation with <see cref="DllAssemblies"/> fails.
        /// </summary>
        public ImmutableDictionary<string, ImmutableArray<byte>>? BuiltInDllAssemblies { get; set; }

        public void Dispose()
        {
            if (LoadContext.IsCollectible)
            {
                LoadContext.Unload();
            }
        }
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
    : AssemblyLoadContext(nameof(CompilerLoader) + iteration, isCollectible: true)
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
