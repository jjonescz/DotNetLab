using System.Collections.Immutable;
using DotNetLab.Lab;
using Fluxor;

namespace DotNetLab.Features.Documents;

[FeatureState]
public sealed record DocumentState
{
    public string Template { get; init; } = "C#";

    public string ActiveDocument { get; init; } = InitialCode.CSharp.SuggestedFileName;

    public ImmutableArray<string> OpenDocuments { get; init; } = [InitialCode.CSharp.SuggestedFileName];

    public DocumentState()
    {
    }
}
