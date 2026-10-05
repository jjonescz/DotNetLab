namespace DotNetLab.Features.Documents;

public static class SpecialDocuments
{
    public const string Directives = "Directives.cs";
    public const string Configuration = "Configuration.cs";
    public static readonly string[] Order = [Directives, Configuration];

    public static string Label(string fileName) => fileName switch
    {
        Directives => "Directives",
        Configuration => "Configuration",
        _ => fileName
    };
}
