using Fluxor;

namespace DotNetLab.Features.Sharing;

public static class PasteUrlDialogReducers
{
    [ReducerMethod]
    public static PasteUrlDialogState Reduce(PasteUrlDialogState state, OpenPasteUrlAction action)
        => state.IsOpen
            ? state
            : state with { IsOpen = true, InitialText = action.InitialText ?? "" };

    [ReducerMethod]
    public static PasteUrlDialogState Reduce(PasteUrlDialogState state, ClosePasteUrlAction _)
        => state.IsOpen ? state with { IsOpen = false, InitialText = "" } : state;
}
