namespace DotNetLab.Features.Compiler;

public sealed record SdkOption(string Value, string Released, string Roslyn, string Razor)
{
    public string Label =>
        string.IsNullOrEmpty(Released) ? Value : $"{Value} — released {Released}";
}
