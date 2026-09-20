using Fluxor;

namespace DotNetLab.Shell.CommandPalette;

public static class CommandPaletteReducers
{
    [ReducerMethod]
    public static CommandPaletteState Reduce(CommandPaletteState state, ToggleCommandPaletteAction _)
        => state with { IsOpen = !state.IsOpen };

    [ReducerMethod]
    public static CommandPaletteState Reduce(CommandPaletteState state, CloseCommandPaletteAction _)
        => state.IsOpen ? state with { IsOpen = false } : state;
}
