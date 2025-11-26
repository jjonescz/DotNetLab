namespace DotNetLab.Lab;

public sealed record CompilerProxyOptions
{
    public bool AssembliesAreAlwaysInDllFormat { get; set; }
    public bool LoadAssembliesFromDisk { get; set; }
}
