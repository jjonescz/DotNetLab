using System.Collections.Immutable;

namespace DotNetLab.Features.Documents;

public sealed record SetDocumentMetadataAction(
    string Template,
    string ActiveDocument,
    ImmutableArray<string> OpenNames);

public sealed record DocumentsChangedAction(IReadOnlyList<string> PreviousUris);

public sealed record ActiveSourceChangedAction;
