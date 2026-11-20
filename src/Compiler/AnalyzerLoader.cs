using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
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

        var loaded = new List<(Assembly Assembly, RefAssembly Analyzer)>();

        foreach (var analyzer in analyzerAssemblies)
        {
            try
            {
                var assembly = GetOrLoadAssembly(alc, analyzer);

                // The same assembly can appear twice in this list.
                // Skip it so its generators run once.
                if (loaded.Any(a => a.Assembly == assembly))
                {
                    logger.LogWarning("Analyzer '{Name}' was already loaded, skipping duplicate.", analyzer.Name);
                    continue;
                }
                
                loaded.Add((assembly, analyzer));
            }
            catch (Exception ex)
            {
                diagnosticBuilder.Add(Diagnostic.Create(AnalyzerLoadFailed, Location.None, analyzer.Name, ex.Message));
                logger.LogWarning(ex, "Failed to load analyzer '{Name}'.", analyzer.Name);
            }
        }

        foreach (var (assembly, analyzer) in loaded)
        {
            foreach (var type in GetLoadableTypes(assembly, analyzer, diagnosticBuilder, logger))
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
        if (!TryReadAssemblyIdentity(analyzer.Bytes, out var incomingVersion, out var incomingModuleVersionId))
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
                var loadedModuleVersionId = loaded.ManifestModule.ModuleVersionId;
                if (loadedModuleVersionId != incomingModuleVersionId)
                {
                    throw new InvalidOperationException(
                        $"Analyzer '{analyzer.Name}' has the same version ({incomingVersion}) but a different module identity ({incomingModuleVersionId}) than the already loaded assembly ({loadedModuleVersionId}).");
                }

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

    private static bool TryReadAssemblyIdentity(ImmutableArray<byte> bytes, out Version? version, out Guid moduleVersionId)
    {
        version = null;
        moduleVersionId = default;

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
        moduleVersionId = reader.GetGuid(reader.GetModuleDefinition().Mvid);
        return true;
    }

    private static IEnumerable<Type> GetLoadableTypes(
        Assembly assembly, RefAssembly analyzer, ImmutableArray<Diagnostic>.Builder diagnostics, ILogger logger)
    {
        using var pe = new PEReader(analyzer.Bytes);
        var reader = pe.GetMetadataReader();
        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            // Unrelated types may depend on missing assemblies or incompatible Roslyn APIs.
            if (!definition.GetCustomAttributes().Any(h => IsExtensionAttribute(reader, h)))
            {
                continue;
            }

            Type type;
            try
            {
                type = assembly.ManifestModule.ResolveType(MetadataTokens.GetToken(handle));
            }
            catch (Exception ex)
            {
                diagnostics.Add(Diagnostic.Create(AnalyzerLoadFailed, Location.None, analyzer.Name, ex.Message));
                logger.LogWarning(ex, "Failed to load analyzer extension type from '{Name}'.", analyzer.Name);
                continue;
            }
            yield return type;
        }
    }

    private static bool IsExtensionAttribute(MetadataReader reader, CustomAttributeHandle handle)
    {
        var attribute = reader.GetCustomAttribute(handle);
        if (attribute.Constructor.Kind != HandleKind.MemberReference)
        {
            return false;
        }

        var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (constructor.Parent.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        return (reader.GetString(type.Namespace), reader.GetString(type.Name)) is
            ("Microsoft.CodeAnalysis", nameof(GeneratorAttribute)) or
            ("Microsoft.CodeAnalysis.Diagnostics", nameof(DiagnosticAnalyzerAttribute)) or
            ("Microsoft.CodeAnalysis.CodeFixes", nameof(ExportCodeFixProviderAttribute));
    }
}
