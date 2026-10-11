using Fluxor;

namespace DotNetLab.Features.Outputs;

public static class OutputReducers
{
    [ReducerMethod]
    public static OutputState Reduce(OutputState state, SetActiveOutputAction action)
    {
        var next = string.IsNullOrEmpty(action.Value) ? "cs" : action.Value;
        return string.Equals(state.ActiveOutput, next, StringComparison.Ordinal)
            ? state
            : state with { ActiveOutput = next };
    }
}
