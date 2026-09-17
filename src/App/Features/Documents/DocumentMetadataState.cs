using System.Collections.Immutable;
using Fluxor;

namespace DotNetLab.Features.Documents;

[FeatureState]
public sealed record DocumentMetadataState
{
    public string Template { get; init; } = "C#";

    public string ActiveDocument { get; init; } = "Program.cs";

    public ImmutableArray<string> OpenNames { get; init; } = ["Program.cs"];

    public DocumentMetadataState()
    {
    }
}
