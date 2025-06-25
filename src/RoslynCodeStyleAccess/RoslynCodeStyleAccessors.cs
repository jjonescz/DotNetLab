namespace DotNetLab;

public static class RoslynCodeStyleAccessors
{
    public static Assembly GetRoslynCodeStyleAssembly()
    {
        return typeof(Microsoft.CodeAnalysis.CSharp.RemoveUnreachableCode.CSharpRemoveUnreachableCodeDiagnosticAnalyzer).Assembly;
    }
}
