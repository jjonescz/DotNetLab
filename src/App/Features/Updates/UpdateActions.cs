namespace DotNetLab.Features.Updates;

public sealed record InitializeUpdatesAction;

public sealed record CheckUpdatesAction;

public sealed record UpdatesSyncedAction(bool Enabled, bool Downloading, bool Available);

public sealed record UpdatesCheckStartedAction;

public sealed record UpdatesCheckFinishedAction(bool Available);
