using DotNetLab.Features.Documents;

namespace DotNetLab.Features.Outputs;

public static class OutputCatalog
{
    public const string ErrorsId = "errors";
    public const string FailId = "fail";
    public const string RazorErrorsId = "razorErrors";

    public static readonly OutputDefinition Tree = new(
        "tree", "Tree", "plaintext", Toolbar: typeof(TreeOutputToolbar));
    public static readonly OutputDefinition Syntax = new("syntax", "Syntax", "plaintext");
    public static readonly OutputDefinition Ir = new("ir", "IR", "csharp");
    public static readonly OutputDefinition RazorErrors = new(
        RazorErrorsId, "Razor Error List", "csharp", Produced: false);
    public static readonly OutputDefinition Gcs = new(
        "gcs", "C#", "csharp", Toolbar: typeof(GeneratedCSharpOutputToolbar));
    public static readonly OutputDefinition Html = new(
        "html",
        "HTML",
        "html",
        Toolbar: typeof(HtmlOutputToolbar),
        View: typeof(HtmlOutputView));
    public static readonly OutputDefinition Il = new(
        "il", "IL", "csharp", Toolbar: typeof(IlOutputToolbar));
    public static readonly OutputDefinition Seq = new("seq", "Seq", "plaintext");
    public static readonly OutputDefinition Cs = new("cs", "C#", "csharp");
    public static readonly OutputDefinition Asm = new("asm", "Asm", "x86");
    public static readonly OutputDefinition Xml = new("xml", "Docs", "xml");
    public static readonly OutputDefinition Run = new("run", "Run", "plaintext");
    public static readonly OutputDefinition Errors = new(
        ErrorsId,
        "Error List",
        "csharp",
        Locked: true,
        Toolbar: typeof(ErrorListToolbar),
        Tab: typeof(ErrorListTab));

    public static readonly OutputDefinition[] CSharp =
    [
        Tree, Il, Seq, Cs, Asm, Xml, Run, Errors,
    ];

    public static readonly OutputDefinition[] Razor =
    [
        Syntax, Ir, RazorErrors, Gcs, Html, Il, Seq, Cs, Asm, Xml, Run, Errors,
    ];

    private static readonly OutputDefinition[] All =
        [.. CSharp.Concat(Razor).DistinctBy(output => output.Id)];

    private static readonly Dictionary<string, OutputDefinition> ById = All.ToDictionary(
        output => output.Id, StringComparer.Ordinal);

    public static OutputDefinition? Get(string id)
        => ById.GetValueOrDefault(id);

    public static OutputDefinition Require(string id)
        => Get(id) ?? new OutputDefinition(id, id == FailId ? "Failure" : id, "plaintext");

    public static string Label(string id) => Require(id).Label;

    public static string Language(string id) => Require(id).Language;

    public static string Title(string id) => Require(id).Title;

    public static bool IsLocked(string id) => Get(id)?.Locked == true;

    public static IReadOnlyList<OutputDefinition> For(DocumentKind kind)
        => kind is DocumentKind.Razor or DocumentKind.Cshtml ? Razor : CSharp;

    public static List<string> DefaultOrder(DocumentKind kind)
        => For(kind).Select(output => output.Id).ToList();

    public static HashSet<string> ProducedTypes(string fileName)
        => For(KindFor(fileName))
            .Where(output => output.Produced)
            .Select(output => output.Id)
            .ToHashSet(StringComparer.Ordinal);

    public static string KindLabel(DocumentKind kind)
        => kind switch
        {
            DocumentKind.Razor => "Razor",
            DocumentKind.Cshtml => "CSHTML",
            _ => "C#"
        };

    public static DocumentKind KindFor(string fileName)
    {
        if (fileName.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentKind.Razor;
        }

        if (fileName.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentKind.Cshtml;
        }

        return DocumentKind.Cs;
    }

    public static string RepresentativeFile(DocumentKind kind)
        => kind switch
        {
            DocumentKind.Razor => "Component.razor",
            DocumentKind.Cshtml => "Page.cshtml",
            _ => "Program.cs"
        };
}
