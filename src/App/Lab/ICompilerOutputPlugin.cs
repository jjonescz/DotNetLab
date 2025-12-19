namespace DotNetLab.Lab;

public interface ICompilerOutputPlugin
{
    string GetText(CompiledFileLazyResult result);
}
