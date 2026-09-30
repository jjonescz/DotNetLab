using Fluxor;

namespace DotNetLab.Features.Workspace;

public static class WorkspaceLayoutReducers
{
    [ReducerMethod]
    public static WorkspaceLayoutState Reduce(WorkspaceLayoutState state, SetSplitAction action)
        => state with { Split = Math.Clamp(action.Value, 25, 75) };
}
