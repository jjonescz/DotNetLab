using Fluxor;

namespace DotNetLab.Features.Documents;

public static class DocumentMetadataReducers
{
    [ReducerMethod]
    public static DocumentMetadataState Reduce(DocumentMetadataState state, SetDocumentMetadataAction action)
    {
        if (string.Equals(state.Template, action.Template, StringComparison.Ordinal) &&
            string.Equals(state.ActiveDocument, action.ActiveDocument, StringComparison.Ordinal) &&
            state.OpenNames.AsSpan().SequenceEqual(action.OpenNames.AsSpan()))
        {
            return state;
        }

        return state with
        {
            Template = action.Template,
            ActiveDocument = action.ActiveDocument,
            OpenNames = action.OpenNames,
        };
    }
}
