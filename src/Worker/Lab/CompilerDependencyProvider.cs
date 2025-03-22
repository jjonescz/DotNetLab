using NuGet.Versioning;
using ProtoBuf;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetLab.Lab;

/// <summary>
/// Provides compiler dependencies into the <see cref="DependencyRegistry"/>.
/// </summary>
/// <remarks>
/// Uses <see cref="ICompilerDependencyResolver"/> plugins.
/// Each plugin can handle one or more <see cref="CompilerVersionSpecifier"/>s.
/// </remarks>
internal sealed class CompilerDependencyProvider(
    DependencyRegistry dependencyRegistry,
    BuiltInCompilerProvider builtInProvider,
    IEnumerable<ICompilerDependencyResolver> resolvers)
{
    private readonly Dictionary<CompilerKind, (CompilerDependencyUserInput UserInput, CompilerDependency Loaded)> loaded = new();

    public async Task<CompilerDependencyInfo> GetLoadedInfoAsync(CompilerKind compilerKind)
    {
        var dependency = loaded.TryGetValue(compilerKind, out var result)
            ? result.Loaded
            : builtInProvider.GetBuiltInDependency(compilerKind);
        return await dependency.Info();
    }

    /// <returns>
    /// <see langword="false"/> if the same version is already used.
    /// </returns>
    public async Task<bool> UseAsync(CompilerKind compilerKind, string? version, BuildConfiguration configuration)
    {
        if (loaded.TryGetValue(compilerKind, out var dependency))
        {
            if (dependency.UserInput.Version == version &&
                dependency.UserInput.Configuration == configuration)
            {
                return false;
            }
        }
        else if (version == null && configuration == default)
        {
            return false;
        }

        var info = CompilerInfo.For(compilerKind);

        var task = findOrThrowAsync();

        // First update the dependency registry so compilation does not start before the search completes.
        dependencyRegistry.Set(info, async () => await (await task).Assemblies());

        await task;

        return true;

        async Task<CompilerDependency> findOrThrowAsync()
        {
            bool any = false;
            List<string>? errors = null;
            CompilerDependency? found = await findAsync();

            if (!any)
            {
                throw new InvalidOperationException($"Nothing could be parsed out of the specified version '{version}'.");
            }

            if (found is null)
            {
                throw new InvalidOperationException($"Specified version was not found.\n{errors?.JoinToString("\n")}");
            }

            var userInput = new CompilerDependencyUserInput
            {
                Version = version,
                Configuration = configuration,
            };

            loaded[compilerKind] = (userInput, found);

            return found;

            async Task<CompilerDependency?> findAsync()
            {
                foreach (var specifier in CompilerVersionSpecifier.Parse(version))
                {
                    any = true;
                    foreach (var plugin in resolvers)
                    {
                        try
                        {
                            if (await plugin.TryResolveCompilerAsync(info, specifier, configuration) is { } dependency)
                            {
                                return dependency;
                            }
                        }
                        catch (Exception ex)
                        {
                            errors ??= new();
                            errors.Add($"{plugin.GetType().Name}: {ex.Message}");
                        }
                    }
                }

                return null;
            }
        }
    }
}

internal interface ICompilerDependencyResolver
{
    /// <returns>
    /// <see langword="null"/> if the <paramref name="specifier"/> is not supported by this resolver.
    /// An exception is thrown if the <paramref name="specifier"/> is supported but the resolution fails.
    /// </returns>
    Task<CompilerDependency?> TryResolveCompilerAsync(
        CompilerInfo info,
        CompilerVersionSpecifier specifier,
        BuildConfiguration configuration);
}

internal sealed class BuiltInCompilerProvider : ICompilerDependencyResolver
{
    private readonly ImmutableDictionary<CompilerKind, CompilerDependency> builtIn = LoadBuiltIn();

    private static ImmutableDictionary<CompilerKind, CompilerDependency> LoadBuiltIn()
    {
        return ImmutableDictionary.CreateRange(Enum.GetValues<CompilerKind>()
            .Select(kind => KeyValuePair.Create(kind, createOne(kind))));

        static CompilerDependency createOne(CompilerKind compilerKind)
        {
            var specifier = new CompilerVersionSpecifier.BuiltIn();
            var info = CompilerInfo.For(compilerKind);
            return new()
            {
                Info = () => Task.FromResult(new CompilerDependencyInfo(assemblyName: info.AssemblyNames[0])
                {
                    VersionSpecifier = specifier,
                    Configuration = BuildConfiguration.Release,
                }),
                Assemblies = () => Task.FromResult(ImmutableArray<LoadedAssembly>.Empty),
            };
        }
    }

    public CompilerDependency GetBuiltInDependency(CompilerKind compilerKind)
    {
        return builtIn.TryGetValue(compilerKind, out var result)
            ? result
            : throw new InvalidOperationException($"Built-in compiler {compilerKind} was not found.");
    }

    public Task<CompilerDependency?> TryResolveCompilerAsync(
        CompilerInfo info,
        CompilerVersionSpecifier specifier,
        BuildConfiguration configuration)
    {
        if (specifier is CompilerVersionSpecifier.BuiltIn)
        {
            return Task.FromResult<CompilerDependency?>(GetBuiltInDependency(info.CompilerKind));
        }

        return Task.FromResult<CompilerDependency?>(null);
    }
}

internal sealed class CompilerDependencyUserInput
{
    public required string? Version { get; init; }
    public required BuildConfiguration Configuration { get; init; }
}

internal sealed class CompilerDependency
{
    public required Func<Task<CompilerDependencyInfo>> Info { get; init; }
    public required Func<Task<ImmutableArray<LoadedAssembly>>> Assemblies { get; init; }
}
