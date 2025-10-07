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
    private readonly Dictionary<CompilerKind, (CompilerDependencyUserInput UserInput, PackageDependency? Loaded)> loaded = new();

    /// <returns>
    /// <see langword="null"/> if last load failed.
    /// </returns>
    public async Task<PackageDependencyInfo?> GetLoadedInfoAsync(CompilerKind compilerKind)
    {
        var dependency = loaded.TryGetValue(compilerKind, out var result)
            ? result.Loaded
            : builtInProvider.GetBuiltInDependency(compilerKind);

        if (dependency is null)
        {
            return null;
        }

        return await dependency.Info.Value;
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
        dependencyRegistry.Set(info, async () => await (await task).Assemblies.Value);

        await task;

        return true;

        async Task<PackageDependency> findOrThrowAsync()
        {
            var userInput = new CompilerDependencyUserInput
            {
                Version = version,
                Configuration = configuration,
            };

            try
            {
                bool any = false;
                List<string>? errors = null;
                PackageDependency? found = await findAsync();

                if (!any)
                {
                    throw new InvalidOperationException($"Nothing could be parsed out of the specified version '{version}'.");
                }

                if (found is null)
                {
                    throw new InvalidOperationException($"Specified version was not found.\n{errors?.JoinToString("\n")}");
                }

                loaded[compilerKind] = (userInput, found);

                return found;

                async Task<PackageDependency?> findAsync()
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
            catch
            {
                // Remember that we did not find anything, so the next call to this method
                // won't incorrectly bail early thinking the version is already successfully loaded.
                loaded[compilerKind] = (userInput, null);

                throw;
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
    Task<PackageDependency?> TryResolveCompilerAsync(
        CompilerInfo info,
        CompilerVersionSpecifier specifier,
        BuildConfiguration configuration);
}

internal sealed class BuiltInCompilerProvider : ICompilerDependencyResolver
{
    private readonly ImmutableDictionary<CompilerKind, PackageDependency> builtIn = LoadBuiltIn();

    private static ImmutableDictionary<CompilerKind, PackageDependency> LoadBuiltIn()
    {
        return ImmutableDictionary.CreateRange(Enum.GetValues<CompilerKind>()
            .Select(kind => KeyValuePair.Create(kind, createOne(kind))));

        static PackageDependency createOne(CompilerKind compilerKind)
        {
            var specifier = new CompilerVersionSpecifier.BuiltIn();
            var info = CompilerInfo.For(compilerKind);
            return new()
            {
                Info = new(() => Task.FromResult(new PackageDependencyInfo(assemblyName: info.AssemblyNames[0],
                    versionLink: (d) => SimpleNuGetUtil.GetPackageDetailUrl(packageId: info.PackageId, version: d.Version, fromNuGetOrg: false))
                {
                    Configuration = BuildConfiguration.Release,
                })),
                Assemblies = new(() => Task.FromResult(ImmutableArray<LoadedAssembly>.Empty)),
            };
        }
    }

    public PackageDependency GetBuiltInDependency(CompilerKind compilerKind)
    {
        return builtIn.TryGetValue(compilerKind, out var result)
            ? result
            : throw new InvalidOperationException($"Built-in compiler {compilerKind} was not found.");
    }

    public Task<PackageDependency?> TryResolveCompilerAsync(
        CompilerInfo info,
        CompilerVersionSpecifier specifier,
        BuildConfiguration configuration)
    {
        if (specifier is CompilerVersionSpecifier.BuiltIn)
        {
            return Task.FromResult<PackageDependency?>(GetBuiltInDependency(info.CompilerKind));
        }

        return Task.FromResult<PackageDependency?>(null);
    }
}

internal sealed class CompilerDependencyUserInput
{
    public required string? Version { get; init; }
    public required BuildConfiguration Configuration { get; init; }
}

internal sealed class PackageDependency
{
    public required Lazy<Task<PackageDependencyInfo>> Info { get; init; }
    public required Lazy<Task<ImmutableArray<LoadedAssembly>>> Assemblies { get; init; }
}
