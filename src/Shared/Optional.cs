namespace DotNetLab;

public readonly union Optional<T>(T, None);

public readonly record struct None;
