namespace DotNetLab.Features.Outputs;

public sealed record OutputTab(string Type, string Label);

public enum OutputFileKind
{
    Cs,
    Razor,
    Cshtml
}
