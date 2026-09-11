using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace DotNetLab;

internal static class PackageGeneratorLoader
{
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

        foreach (var analyzer in analyzerAssemblies)
        {
            Assembly assembly;
            try
            {
                assembly = GetOrLoadAssembly(alc, analyzer);
            }
            catch (Exception ex)
            {
                diagnosticBuilder.Add(CreateLoadDiagnostic($"Failed to load analyzer '{analyzer.Name}': {ex.Message}"));
                logger.LogWarning(ex, "Failed to load analyzer '{Name}'.", analyzer.Name);
                continue;
            }

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
                        diagnosticBuilder.Add(CreateLoadDiagnostic(
                            $"Type '{type.FullName}' in '{analyzer.Name}' has [Generator] but does not implement IIncrementalGenerator or ISourceGenerator."));
                    }
                }
                catch (Exception ex)
                {
                    diagnosticBuilder.Add(CreateLoadDiagnostic(
                        $"Failed to instantiate source generator '{type.FullName}' from '{analyzer.Name}': {ex.Message}"));
                    logger.LogWarning(ex, "Failed to instantiate source generator '{Type}' from '{Name}'.", type.FullName, analyzer.Name);
                }
            }
        }

        diagnostics = diagnosticBuilder.DrainToImmutable();
        return generators.DrainToImmutable();
    }

    private static Assembly GetOrLoadAssembly(AssemblyLoadContext alc, RefAssembly analyzer)
    {
        foreach (var loaded in alc.Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, analyzer.Name, StringComparison.OrdinalIgnoreCase))
            {
                return loaded;
            }
        }

        return alc.LoadFromStream(new MemoryStream(ImmutableCollectionsMarshal.AsArray(analyzer.Bytes)!));
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

    private static Diagnostic CreateLoadDiagnostic(string message) => Diagnostic.Create(
        id: "LAB",
        category: "SourceGenerator",
        message: message,
        DiagnosticSeverity.Warning,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        warningLevel: 1,
        location: Location.None);
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
