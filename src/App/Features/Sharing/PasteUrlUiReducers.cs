using Fluxor;

namespace DotNetLab.Features.Sharing;

public static class PasteUrlUiReducers
{
    [ReducerMethod]
    public static PasteUrlUiState Reduce(PasteUrlUiState state, OpenPasteUrlAction _)
        => state.IsOpen ? state : state with { IsOpen = true };

    [ReducerMethod]
    public static PasteUrlUiState Reduce(PasteUrlUiState state, ClosePasteUrlAction _)
        => state.IsOpen ? state with { IsOpen = false } : state;
}
