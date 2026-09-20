namespace DotNetLab.Features.Documents;

public static class BuiltInContent
{
    public const string DirectivesFileName = "Directives.cs";
    public const string ConfigurationFileName = "Configuration.cs";
    public static readonly string[] SpecialSourceOrder = [DirectivesFileName, ConfigurationFileName];
}
