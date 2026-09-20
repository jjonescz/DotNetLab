using System.Collections.Immutable;
using Fluxor;

namespace DotNetLab.Features.Documents;

[FeatureState]
public sealed record DocumentState
{
    public string Template { get; init; } = "C#";

    public string ActiveDocument { get; init; } = "Program.cs";

    public ImmutableArray<string> OpenDocuments { get; init; } = ["Program.cs"];

    public DocumentState()
    {
    }
}
