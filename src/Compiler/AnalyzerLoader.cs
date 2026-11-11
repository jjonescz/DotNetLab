using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DotNetLab;

internal static class AnalyzerLoader
{
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

    private static readonly DiagnosticDescriptor ExtensionInstantiateFailed = new(
        id: "LAB",
        title: "Analyzer extension load",
        messageFormat: "Failed to instantiate analyzer extension '{0}' from '{1}': {2}",
        category: "Analyzer",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static AnalyzerReference Load(
        AssemblyLoadContext alc,
        ImmutableArray<RefAssembly> analyzerAssemblies,
        ILogger logger,
        out ImmutableArray<Diagnostic> diagnostics)
    {
        if (analyzerAssemblies.IsDefaultOrEmpty)
        {
            diagnostics = [];
            return RoslynWorkspaceAccessors.CreateAnalyzerReference([], [], []);
        }

        var generators = ImmutableArray.CreateBuilder<ISourceGenerator>();
        var analyzers = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        var codeFixes = ImmutableArray.CreateBuilder<CodeFixProvider>();
        var diagnosticBuilder = ImmutableArray.CreateBuilder<Diagnostic>();

        var loaded = new List<Assembly>();

        foreach (var analyzer in analyzerAssemblies)
        {
            try
            {
                var assembly = GetOrLoadAssembly(alc, analyzer);

                // The same assembly can appear twice in this list.
                // Skip it so its generators run once.
                if (loaded.Contains(assembly))
                {
                    logger.LogWarning("Analyzer '{Name}' was already loaded, skipping duplicate.", analyzer.Name);
                    continue;
                }
                
                loaded.Add(assembly);
            }
            catch (Exception ex)
            {
                diagnosticBuilder.Add(Diagnostic.Create(AnalyzerLoadFailed, Location.None, analyzer.Name, ex.Message));
                logger.LogWarning(ex, "Failed to load analyzer '{Name}'.", analyzer.Name);
            }
        }

        foreach (var assembly in loaded)
        {
            foreach (var type in GetLoadableTypes(assembly, diagnosticBuilder, logger))
            {
                if (type is not { IsClass: true, IsAbstract: false } ||
                    type.ContainsGenericParameters)
                {
                    continue;
                }

                try
                {
                    bool isGenerator = type.GetCustomAttribute<GeneratorAttribute>()?.Languages.Contains(LanguageNames.CSharp) == true;
                    bool isAnalyzer = type.IsSubclassOf(typeof(DiagnosticAnalyzer)) &&
                        type.GetCustomAttribute<DiagnosticAnalyzerAttribute>()?.Languages.Contains(LanguageNames.CSharp) == true;
                    bool isCodeFix = type.IsSubclassOf(typeof(CodeFixProvider)) &&
                        type.GetCustomAttribute<ExportCodeFixProviderAttribute>()?.Languages.Contains(LanguageNames.CSharp) == true;
                    if (!isGenerator && !isAnalyzer && !isCodeFix)
                    {
                        continue;
                    }

                    object instance = Activator.CreateInstance(type)!;
                    if (isGenerator)
                    {
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
                    if (isAnalyzer)
                    {
                        analyzers.Add((DiagnosticAnalyzer)instance);
                    }
                    if (isCodeFix)
                    {
                        codeFixes.Add((CodeFixProvider)instance);
                    }
                }
                catch (Exception ex)
                {
                    diagnosticBuilder.Add(Diagnostic.Create(ExtensionInstantiateFailed, Location.None, type.FullName,
                        assembly.GetName().Name, ex.GetBaseException().Message));
                    logger.LogWarning(ex, "Failed to instantiate analyzer extension '{Type}' from '{Name}'.", type.FullName, assembly.GetName().Name);
                }
            }
        }

        diagnostics = diagnosticBuilder.DrainToImmutable();
        return RoslynWorkspaceAccessors.CreateAnalyzerReference(
            analyzers.DrainToImmutable(), generators.DrainToImmutable(), codeFixes.DrainToImmutable());
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

    private static IEnumerable<Type> GetLoadableTypes(
        Assembly assembly, ImmutableArray<Diagnostic>.Builder diagnostics, ILogger logger)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var error in ex.LoaderExceptions.OfType<Exception>())
            {
                diagnostics.Add(Diagnostic.Create(AnalyzerLoadFailed, Location.None, assembly.GetName().Name, error.Message));
                logger.LogWarning(error, "Failed to load types from analyzer '{Name}'.", assembly.GetName().Name);
            }
            return ex.Types.OfType<Type>();
        }
    }
}
