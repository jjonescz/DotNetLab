namespace DotNetLab.Features.Outputs;

public static class OutputCatalog
{
    public const string ErrorsId = "errors";
    public const string FailId = "fail";
    public const string RazorErrorsId = "razorErrors";

    public static readonly LabOutput Tree = new(
        "tree", "Tree", "plaintext", Toolbar: typeof(TreeOutputToolbar));
    public static readonly LabOutput Syntax = new("syntax", "Syntax", "plaintext");
    public static readonly LabOutput Ir = new("ir", "IR", "csharp");
    public static readonly LabOutput RazorErrors = new(
        RazorErrorsId, "Razor Error List", "csharp", Produced: false);
    public static readonly LabOutput Gcs = new(
        "gcs", "C#", "csharp", Toolbar: typeof(GcsOutputToolbar));
    public static readonly LabOutput Html = new(
        "html",
        "HTML",
        "html",
        Toolbar: typeof(HtmlOutputToolbar),
        View: typeof(HtmlOutputView));
    public static readonly LabOutput Il = new(
        "il", "IL", "csharp", Toolbar: typeof(IlOutputToolbar));
    public static readonly LabOutput Seq = new("seq", "Seq", "plaintext");
    public static readonly LabOutput Cs = new("cs", "C#", "csharp");
    public static readonly LabOutput Asm = new("asm", "Asm", "x86");
    public static readonly LabOutput Xml = new("xml", "Docs", "xml");
    public static readonly LabOutput Run = new("run", "Run", "plaintext");
    public static readonly LabOutput Errors = new(
        ErrorsId,
        "Error List",
        "csharp",
        Locked: true,
        Toolbar: typeof(ErrorsOutputToolbar),
        Tab: typeof(ErrorsOutputTab));

    public static readonly LabOutput[] CSharp =
    [
        Tree, Il, Seq, Cs, Asm, Xml, Run, Errors,
    ];

    public static readonly LabOutput[] Razor =
    [
        Syntax, Ir, RazorErrors, Gcs, Html, Il, Seq, Cs, Asm, Xml, Run, Errors,
    ];

    private static readonly LabOutput[] All =
        [.. CSharp.Concat(Razor).DistinctBy(output => output.Id)];

    private static readonly Dictionary<string, LabOutput> ById = All.ToDictionary(
        output => output.Id, StringComparer.Ordinal);

    public static LabOutput? Get(string id)
        => ById.GetValueOrDefault(id);

    public static LabOutput Require(string id)
        => Get(id) ?? new LabOutput(id, id == FailId ? "Failure" : id, "plaintext");

    public static string Label(string id) => Require(id).Label;

    public static string Language(string id) => Require(id).Language;

    public static string Title(string id) => Require(id).Title;

    public static bool IsLocked(string id) => Get(id)?.Locked == true;

    public static IReadOnlyList<LabOutput> For(OutputFileKind kind)
        => kind is OutputFileKind.Razor or OutputFileKind.Cshtml ? Razor : CSharp;

    public static List<string> DefaultOrder(OutputFileKind kind)
        => For(kind).Select(output => output.Id).ToList();

    public static HashSet<string> ProducedTypes(string fileName)
        => For(KindFor(fileName))
            .Where(output => output.Produced)
            .Select(output => output.Id)
            .ToHashSet(StringComparer.Ordinal);

    public static string KindLabel(OutputFileKind kind)
        => kind switch
        {
            OutputFileKind.Razor => "Razor",
            OutputFileKind.Cshtml => "CSHTML",
            _ => "C#"
        };

    public static OutputFileKind KindFor(string fileName)
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

    public static string RepresentativeFile(OutputFileKind kind)
        => kind switch
        {
            OutputFileKind.Razor => "Component.razor",
            OutputFileKind.Cshtml => "Page.cshtml",
            _ => "Program.cs"
        };
}
