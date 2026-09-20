namespace DotNetLab.Features.Compiler;

public static class CompilerCatalog
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
}
