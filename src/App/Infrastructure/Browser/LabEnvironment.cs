namespace DotNetLab.Infrastructure.Browser;

/// <param name="SupportsThreads">
/// Real .NET threads exist (native / test host). Browser WASM stays false.
/// </param>
public sealed record LabEnvironment(
    bool IsDevelopment,
    string BaseAddress,
    bool SupportsThreads = false);
