using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotNetLab;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SupportedPlatformAttributeParityAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = DiagnosticIds.SupportedPlatformAttributeParityAnalyzer;

    private static readonly LocalizableString Title =
        "Method should be annotated with SupportedOSPlatform to match attribute requirement";

    private static readonly LocalizableString MessageFormat =
        "Method '{0}' uses attribute '{1}' that requires platform '{2}', " +
        "but the method is not annotated with [SupportedOSPlatform(\"{2}\")]";

    private static readonly LocalizableString Description =
        "If an attribute type is annotated with [SupportedOSPlatform(\"platform\")], " +
        "any method using that attribute should also be annotated with the same " +
        "SupportedOSPlatform to make platform constraints explicit to callers.";

    private const string Category = "Interoperability";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    private static readonly ImmutableArray<DiagnosticDescriptor> s_supportedDiagnostics = [Rule];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => s_supportedDiagnostics;

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(StartAnalysis);
    }

    private static void StartAnalysis(CompilationStartAnalysisContext ctx)
    {
        var supportedOsPlatformAttr =
            ctx.Compilation.GetTypeByMetadataName("System.Runtime.Versioning.SupportedOSPlatformAttribute");

        if (supportedOsPlatformAttr is null)
        {
            return;
        }

        ctx.RegisterSymbolAction(symbolCtx =>
        {
            var method = (IMethodSymbol)symbolCtx.Symbol;

            // Collect the platforms the method itself declares.
            var methodPlatforms = CollectSupportedPlatforms(method, supportedOsPlatformAttr)
                .Concat(CollectSupportedPlatforms(method.ContainingType, supportedOsPlatformAttr))
                .ToArray();

            // Scan attributes applied to the method.
            foreach (var usage in method.GetAttributes())
            {
                var attributeType = usage.AttributeClass;
                if (attributeType is null)
                    continue;

                // Platforms required by the attribute type.
                var attrTypePlatforms = CollectSupportedPlatforms(attributeType, supportedOsPlatformAttr);
                if (attrTypePlatforms.Count == 0)
                    continue;

                // For diagnostics location, prefer the attribute usage syntax if available.
                var location = usage.ApplicationSyntaxReference?.GetSyntax(symbolCtx.CancellationToken)?.GetLocation()
                    ?? method.Locations.FirstOrDefault();

                // For each platform required by the attribute type, the method must declare the same platform.
                foreach (var platform in attrTypePlatforms)
                {
                    if (!HasPlatform(methodPlatforms, platform))
                    {
                        var diag = Diagnostic.Create(
                            descriptor: Rule,
                            location: location,
                            messageArgs:
                            [
                                method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                attributeType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                platform,
                            ]);
                        symbolCtx.ReportDiagnostic(diag);
                    }
                }
            }
        }, SymbolKind.Method);
    }

    /// <summary>
    /// Collects all platform strings from <c>[SupportedOSPlatform("…")]</c> on a symbol (type/method).
    /// </summary>
    private static List<string> CollectSupportedPlatforms(
        ISymbol symbol,
        INamedTypeSymbol supportedOsPlatformAttr)
    {
        var list = new List<string>();

        foreach (var attr in symbol.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, supportedOsPlatformAttr))
                continue;

            if (attr.ConstructorArguments.Length == 1 &&
                attr.ConstructorArguments[0].Kind == TypedConstantKind.Primitive &&
                attr.ConstructorArguments[0].Value is string s &&
                !string.IsNullOrWhiteSpace(s))
            {
                list.Add(s);
            }
        }

        return list;
    }

    private static bool HasPlatform(IReadOnlyCollection<string> declaredPlatforms, string requiredPlatform)
    {
        // Simple, exact (case-insensitive) match; extend here if you want version-aware logic.
        return declaredPlatforms.Any(p => string.Equals(p, requiredPlatform, StringComparison.OrdinalIgnoreCase));
    }
}
