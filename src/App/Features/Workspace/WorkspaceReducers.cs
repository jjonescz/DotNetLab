using Fluxor;

namespace DotNetLab.Features.Workspace;

public static class WorkspaceReducers
{
    [ReducerMethod]
    public static WorkspaceState Reduce(WorkspaceState state, SetSplitAction action)
        => state with { Split = Math.Clamp(action.Value, 25, 75) };
}
