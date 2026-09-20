namespace DotNetLab.Features.Documents;

public enum DocumentKind
{
    Cs,
    Razor,
    Cshtml
}

public static class DocumentTypes
{
    public static readonly string[] Templates = ["C#", "Razor", "CSHTML"];

    public static bool IsRazorLike(string fileName)
        => fileName.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ||
           fileName.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

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
