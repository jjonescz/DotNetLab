using Fluxor;

namespace DotNetLab.Features.Updates;

public static class UpdateReducers
{
    [ReducerMethod]
    public static UpdateState Reduce(UpdateState state, UpdatesSyncedAction action)
        => state with
        {
            Enabled = action.Enabled,
            Downloading = action.Downloading,
            Available = action.Available,
        };

    [ReducerMethod]
    public static UpdateState Reduce(UpdateState state, UpdatesCheckStartedAction _)
        => state with
        {
            Checking = true,
            CheckCompleted = false,
        };

    [ReducerMethod]
    public static UpdateState Reduce(UpdateState state, UpdatesCheckFinishedAction action)
        => state with
        {
            Checking = false,
            CheckCompleted = true,
            Available = action.Available,
        };
}
