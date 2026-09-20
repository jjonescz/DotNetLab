using Fluxor;

namespace DotNetLab.Features.Sharing;

public static class PasteUrlDialogReducers
{
    [ReducerMethod]
    public static PasteUrlDialogState Reduce(PasteUrlDialogState state, OpenPasteUrlAction _)
        => state.IsOpen ? state : state with { IsOpen = true };

    [ReducerMethod]
    public static PasteUrlDialogState Reduce(PasteUrlDialogState state, ClosePasteUrlAction _)
        => state.IsOpen ? state with { IsOpen = false } : state;
}
