namespace DotNetLab.Features.Outputs;

[Flags]
public enum OutputKinds
{
    None = 0,
    Cs = 1 << OutputFileKind.Cs,
    Razor = 1 << OutputFileKind.Razor,
    Cshtml = 1 << OutputFileKind.Cshtml,
    RazorLike = Razor | Cshtml,
    All = Cs | Razor | Cshtml,
}

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
    OutputKinds Kinds,
    bool Locked = false,
    bool Produced = true,
    Type? Toolbar = null,
    Type? View = null,
    Type? Tab = null)
{
    public bool Supports(OutputFileKind kind)
        => (Kinds & (OutputKinds)(1 << (int)kind)) != 0;

    public string Title
        => Id == "seq" ? "Sequence points (seq)" : $"{Label} ({Id})";
}
