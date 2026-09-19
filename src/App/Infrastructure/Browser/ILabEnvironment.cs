namespace DotNetLab.Infrastructure.Browser;

public interface ILabEnvironment
{
    bool IsDevelopment { get; }

    string BaseAddress { get; }

    /// <summary>
    /// Real .NET threads exist (native / test host). Browser WASM stays false.
    /// </summary>
    bool SupportsThreads { get; }
}

public sealed record LabEnvironment(
    bool IsDevelopment,
    string BaseAddress,
    bool SupportsThreads = false) : ILabEnvironment;
