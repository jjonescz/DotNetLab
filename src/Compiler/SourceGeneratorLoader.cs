using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace DotNetLab;

internal static class SourceGeneratorLoader
{
#pragma warning disable RS2008 // Compiler is not a shipped analyzer package
    private static readonly DiagnosticDescriptor AnalyzerLoadFailed = new(
        id: "LAB",
        title: "Analyzer load",
        messageFormat: "Failed to load analyzer '{0}': {1}",
        category: "Analyzer",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor GeneratorTypeInvalid = new(
        id: "LAB",
        title: "Source generator load",
        messageFormat: "Type '{0}' in '{1}' has [Generator] but does not implement IIncrementalGenerator or ISourceGenerator",
        category: "SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor GeneratorInstantiateFailed = new(
        id: "LAB",
        title: "Source generator load",
        messageFormat: "Failed to instantiate source generator '{0}' from '{1}': {2}",
        category: "SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
#pragma warning restore RS2008
    
    public static ImmutableArray<ISourceGenerator> Load(
        AssemblyLoadContext alc,
        ImmutableArray<RefAssembly> analyzerAssemblies,
        ILogger logger,
        out ImmutableArray<Diagnostic> diagnostics)
    {
        if (analyzerAssemblies.IsDefaultOrEmpty)
        {
            diagnostics = [];
            return [];
        }

        var generators = ImmutableArray.CreateBuilder<ISourceGenerator>();
        var diagnosticBuilder = ImmutableArray.CreateBuilder<Diagnostic>();

        var loaded = new List<Assembly>();

        foreach (var analyzer in analyzerAssemblies)
        {
            try
            {
                loaded.Add(GetOrLoadAssembly(alc, analyzer));
            }
            catch (Exception ex)
            {
                diagnosticBuilder.Add(Diagnostic.Create(AnalyzerLoadFailed, Location.None, analyzer.Name, ex.Message));
                logger.LogWarning(ex, "Failed to load analyzer '{Name}'.", analyzer.Name);
            }
        }

        foreach (var assembly in loaded)
        {
            foreach (var type in GetLoadableTypes(assembly))
            {
                if (type is not { IsClass: true, IsAbstract: false } ||
                    type.ContainsGenericParameters ||
                    !HasGeneratorAttribute(type))
                {
                    continue;
                }

                try
                {
                    object instance = Activator.CreateInstance(type)!;
                    if (instance is IIncrementalGenerator incremental)
                    {
                        generators.Add(incremental.AsSourceGenerator());
                    }
                    else if (instance is ISourceGenerator source)
                    {
                        generators.Add(source);
                    }
                    else
                    {
                        diagnosticBuilder.Add(Diagnostic.Create(GeneratorTypeInvalid, Location.None, type.FullName, assembly.GetName().Name));
                    }
                }
                catch (Exception ex)
                {
                    diagnosticBuilder.Add(Diagnostic.Create(GeneratorInstantiateFailed, Location.None, type.FullName,
                        assembly.GetName().Name, ex.Message));
                    logger.LogWarning(ex, "Failed to instantiate source generator '{Type}' from '{Name}'.", type.FullName, assembly.GetName().Name);
                }
            }
        }

        diagnostics = diagnosticBuilder.DrainToImmutable();
        return generators.DrainToImmutable();
    }

    private static Assembly GetOrLoadAssembly(AssemblyLoadContext alc, RefAssembly analyzer)
    {
        if (!TryReadAssemblyVersion(analyzer.Bytes, out var incomingVersion))
        {
            throw new InvalidOperationException($"Analyzer '{analyzer.Name}' does not contain valid assembly metadata.");
        }

        Assembly? sameName = null;
        foreach (var loaded in alc.Assemblies)
        {
            var loadedName = loaded.GetName();
            if (!string.Equals(loadedName.Name, analyzer.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sameName = loaded;
            if (loadedName.Version == incomingVersion)
            {
                return loaded;
            }
        }

        if (sameName is not null)
        {
            throw new InvalidOperationException(
                $"Analyzer '{analyzer.Name}' has a different version ({incomingVersion}) than the already loaded assembly ({sameName.GetName().Version}).");
        }

        return alc.LoadFromStream(new MemoryStream(ImmutableCollectionsMarshal.AsArray(analyzer.Bytes)!));
    }

    private static bool TryReadAssemblyVersion(ImmutableArray<byte> bytes, out Version? version)
    {
        version = null;

        if (bytes.IsDefaultOrEmpty)
        {
            return false;
        }

        var image = ImmutableCollectionsMarshal.AsArray(bytes)!;
        using var stream = new MemoryStream(image, writable: false);
        using var pe = new PEReader(stream);

        if (!pe.HasMetadata)
        {
            return false;
        }

        var reader = pe.GetMetadataReader();
        if (!reader.IsAssembly)
        {
            return false;
        }

        version = reader.GetAssemblyDefinition().Version;
        return true;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static t => t is not null)!;
        }
    }

    private static bool HasGeneratorAttribute(Type type) =>
        type.GetCustomAttributesData().Any(static a =>
            a.AttributeType.FullName == "Microsoft.CodeAnalysis.GeneratorAttribute");
}

internal sealed class PackageGeneratorAnalyzerReference(ImmutableArray<ISourceGenerator> generators) : AnalyzerReference
{
    public override string Display => "NuGet source generators";
    public override string? FullPath => null;
    public override object Id { get; } = new object();

    public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];
    public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];
    public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages() => generators;
    public override ImmutableArray<ISourceGenerator> GetGenerators(string language)
        => language == LanguageNames.CSharp ? generators : [];
}
