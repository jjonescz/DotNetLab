using DotNetLab.Lab;

namespace DotNetLab.Features.Compiler;

public static class CompilerSpec
{
    public static string? ToSpecifier(string? value)
        => string.IsNullOrWhiteSpace(value) ||
           string.Equals(value.Trim(), "built-in", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();

    public static string Display(string? value) => ToSpecifier(value) ?? "built-in";

    public static BuildConfiguration ToBuildConfiguration(string value)
        => string.Equals(value, "Debug", StringComparison.OrdinalIgnoreCase)
            ? BuildConfiguration.Debug
            : BuildConfiguration.Release;
}
