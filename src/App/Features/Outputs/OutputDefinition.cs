namespace DotNetLab.Features.Outputs;

public sealed record OutputDefinition(
    string Id,
    string Label,
    string Language,
    bool Locked = false,
    bool Produced = true,
    Type? Toolbar = null,
    Type? View = null,
    Type? Tab = null);
