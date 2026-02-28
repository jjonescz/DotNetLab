using Microsoft.CodeAnalysis.CSharp;

namespace DotNetLab;

[TestClass]
public sealed class TreeFormatterTests : VerifyBase
{
    private static string Format(
        [StringSyntax("C#")] string code,
        bool showBoundNodes = false)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create(
            "Test",
            [tree],
            RefAssemblyMetadata.All);
        var model = compilation.GetSemanticModel(tree);

        var formatter = new TreeFormatter();
        var result = formatter.Format(model, tree.GetRoot(), new()
        {
            ExcludeSymbols = true,
            ExcludeOperations = true,
            ExcludeBoundNodes = !showBoundNodes,
        });

        return result.Text;
    }

    [TestMethod]
    public Task SkippedTrivia() => Verify(Format("var x = [1; 2; 3];"));

    [TestMethod]
    public Task BoundBody() => Verify(Format("class C { int M() => 1; }", showBoundNodes: true));
}
