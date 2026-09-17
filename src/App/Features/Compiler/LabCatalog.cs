using DotNetLab.Features.Outputs;

namespace DotNetLab.Features.Compiler;

public static class LabCatalog
{
    public static readonly SdkOption[] SdkVersions =
    [
        new("10.0.100", "10.0.100 — released 2025-11-11", "5.0.0-1.25517.3", "10.0.0-rc.2.25502.107"),
        new("10.0.100-rc.2", "10.0.100-rc.2 — released 2025-10-14", "5.0.0-1.25476.2", "10.0.0-rc.2.25476.2"),
        new("10.0.100-rc.1", "10.0.100-rc.1 — released 2025-09-09", "5.0.0-1.25431.1", "10.0.0-rc.1.25431.1"),
        new("9.0.305", "9.0.305 — released 2025-08-12", "4.14.0-3.25302.2", "9.0.0-rc.1.24431.7"),
        new("9.0.301", "9.0.301 — released 2025-06-10", "4.14.0-3.25218.8", "9.0.0-preview.4.24267.3"),
        new("9.0.203", "9.0.203 — released 2025-04-08", "4.13.0-3.25167.3", "9.0.0-preview.3.24204.2"),
        new("8.0.414", "8.0.414 — released 2025-08-12", "4.11.0-3.25302.2", "8.0.0"),
        new("8.0.408", "8.0.408 — released 2025-05-13", "4.11.0-3.25218.8", "8.0.0")
    ];

    public static readonly string[] CompilerRefs = ["latest", "main", "built-in"];
    public static readonly RazorToolchain[] RazorToolchainOptions =
    [
        RazorToolchain.SourceGeneratorOrInternalApi,
        RazorToolchain.SourceGenerator,
        RazorToolchain.InternalApi,
    ];
    public static readonly RazorStrategy[] RazorStrategyOptions =
    [
        RazorStrategy.Runtime,
        RazorStrategy.DesignTime,
    ];
    public static readonly string[] Templates = ["C#", "Razor", "CSHTML"];
    public static readonly SymbolDisplayKinds[] SymbolDisplayKindOptions =
    [
        SymbolDisplayKinds.None,
        SymbolDisplayKinds.Public,
        SymbolDisplayKinds.Internal,
        SymbolDisplayKinds.Both,
    ];

    public static string RazorToolchainLabel(RazorToolchain value)
        => value switch
        {
            RazorToolchain.SourceGenerator => "Source Generator",
            RazorToolchain.InternalApi => "Internal API",
            _ => "Auto",
        };

    public static string RazorStrategyLabel(RazorStrategy value)
        => value switch
        {
            RazorStrategy.DesignTime => "DesignTime",
            _ => "Runtime",
        };

    public static string SymbolDisplayKindLabel(SymbolDisplayKinds value)
        => value switch
        {
            SymbolDisplayKinds.Public => "Public Symbols",
            SymbolDisplayKinds.Internal => "Internal Symbols",
            SymbolDisplayKinds.Both => "All Symbols",
            _ => "No Symbols",
        };

    public const string ErrorsOutputType = "errors";
    public const string FailOutputType = "fail";
    public const string RazorErrorsOutputType = "razorErrors";

    public static readonly OutputTab[] GlobalOutputTabs =
    [
        new("il", "IL"),
        new("seq", "Seq"),
        new("cs", "C#"),
        new("asm", "Asm"),
        new("xml", "Docs"),
        new("run", "Run"),
        new(ErrorsOutputType, "Error List")
    ];

    public static readonly OutputTab[] CsharpOutputCatalog =
    [
        new("tree", "Tree"),
        .. GlobalOutputTabs
    ];

    public static readonly OutputTab[] RazorLikeOutputCatalog =
    [
        new("syntax", "Syntax"),
        new("ir", "IR"),
        new(RazorErrorsOutputType, "Razor Error List"),
        new("gcs", "C#"),
        new("html", "HTML"),
        .. GlobalOutputTabs
    ];

    public static string OutputKindLabel(OutputFileKind kind)
        => kind switch
        {
            OutputFileKind.Razor => "Razor",
            OutputFileKind.Cshtml => "CSHTML",
            _ => "C#"
        };

    public static OutputFileKind OutputKindFor(string fileName)
    {
        if (fileName.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
        {
            return OutputFileKind.Razor;
        }

        if (fileName.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
        {
            return OutputFileKind.Cshtml;
        }

        return OutputFileKind.Cs;
    }

    public static IReadOnlyList<OutputTab> CatalogFor(OutputFileKind kind)
        => kind is OutputFileKind.Razor or OutputFileKind.Cshtml
            ? RazorLikeOutputCatalog
            : CsharpOutputCatalog;

    public static bool IsOutputTabLocked(string type)
        => type is ErrorsOutputType;

    public static bool IsRazorLike(string fileName)
        => fileName.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ||
           fileName.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

    public static List<string> DefaultTabOrder(OutputFileKind kind)
        => CatalogFor(kind).Select(tab => tab.Type).ToList();

    public static HashSet<string> ProducedOutputTypes(string fileName)
        => CatalogFor(OutputKindFor(fileName))
            .Select(tab => tab.Type)
            .Where(type => type != RazorErrorsOutputType && type != FailOutputType)
            .ToHashSet(StringComparer.Ordinal);

    public static string RepresentativeFile(OutputFileKind kind)
        => kind switch
        {
            OutputFileKind.Razor => "Component.razor",
            OutputFileKind.Cshtml => "Page.cshtml",
            _ => "Program.cs"
        };

    public static string OutputTypeLabel(string type)
    {
        foreach (var tab in RazorLikeOutputCatalog)
        {
            if (tab.Type == type)
            {
                return tab.Label;
            }
        }

        foreach (var tab in CsharpOutputCatalog)
        {
            if (tab.Type == type)
            {
                return tab.Label;
            }
        }

        return type switch
        {
            FailOutputType => "Failure",
            _ => type
        };
    }

    public static string OutputLanguage(string type)
        => type switch
        {
            "cs" or "gcs" or "il" or "ir" or "errors" => "csharp",
            "html" => "html",
            "xml" => "xml",
            _ => "plaintext"
        };

    public static string LanguageFor(string fileName)
    {
        if (fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return "csharp";
        }

        if (IsRazorLike(fileName))
        {
            return "razor";
        }

        return "plaintext";
    }
}
