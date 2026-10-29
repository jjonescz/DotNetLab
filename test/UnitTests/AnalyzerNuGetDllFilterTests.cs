using AwesomeAssertions;
using DotNetLab.Lab;

namespace DotNetLab;

[TestClass]
public sealed class AnalyzerNuGetDllFilterTests
{
    private static readonly Version Roslyn48 = new(4, 8, 0);
    
    private static string[] Include(Version compiler, params string[] files)
    {
        var filter = new AnalyzerNuGetDllFilter(compiler).GetFilter(files, "Test.Package");
        return [.. files.Where(filter)];
    }
    
    [TestMethod]
    public void IncludesLanguageNeutralHelpersWithCSharpAnalyzers()
    {
        var included = Include(
            Roslyn48,
            "analyzers/dotnet/Helper.dll",
            "analyzers/dotnet/cs/CSharp.dll",
            "lib/net8.0/Lib.dll");
        
        included.Should().Equal(
            "analyzers/dotnet/Helper.dll",
            "analyzers/dotnet/cs/CSharp.dll");
    }
    
    [TestMethod]
    public void KeepsNewestCompatibleRoslynFolder_SkipsOlderAndNewer()
    {
        var included = Include(
            Roslyn48,
            "analyzers/dotnet/roslyn3.11/cs/Old.dll",
            "analyzers/dotnet/roslyn4.4/cs/Current.dll",
            "analyzers/dotnet/roslyn4.12/cs/TooNew.dll");
        
        included.Should().Equal("analyzers/dotnet/roslyn4.4/cs/Current.dll");
    }
    
    [TestMethod]
    public void KeepsHelpersWhenRoslynFolderIsSelected_SkipsUnversionedCSharp()
    {
        var included = Include(
            Roslyn48,
            "analyzers/dotnet/Helper.dll",
            "analyzers/dotnet/cs/Unversioned.dll",
            "analyzers/dotnet/roslyn4.4/cs/Versioned.dll");
        
        included.Should().Equal(
            "analyzers/dotnet/Helper.dll",
            "analyzers/dotnet/roslyn4.4/cs/Versioned.dll");
    }
    
    [TestMethod]
    public void SkipsVisualBasic()
    {
        var included = Include(
            Roslyn48,
            "analyzers/dotnet/cs/CSharp.dll",
            "analyzers/dotnet/vb/VisualBasic.dll");
        
        included.Should().Equal("analyzers/dotnet/cs/CSharp.dll");
    }
    
    [TestMethod]
    public void ReturnsNoneWhenNoAnalyzerDlls()
    {
        Include(Roslyn48, "lib/net8.0/Lib.dll").Should().BeEmpty();
    }
}
