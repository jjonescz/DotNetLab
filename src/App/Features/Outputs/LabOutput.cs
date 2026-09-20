namespace DotNetLab.Features.Outputs;

public enum OutputFileKind
{
    Cs,
    Razor,
    Cshtml
}

public sealed record LabOutput(
    string Id,
    string Label,
    string Language,
    bool Locked = false,
    bool Produced = true,
    Type? Toolbar = null,
    Type? View = null,
    Type? Tab = null)
{
    public string Title
        => Id == "seq" ? "Sequence points (seq)" : $"{Label} ({Id})";
}
