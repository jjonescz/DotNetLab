using Microsoft.CodeAnalysis.CSharp;

namespace DotNetLab;

public sealed class TreeFormatterTests(ITestOutputHelper output)
{
    [Fact]
    public void SyntaxTree()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            #define TEST

            using System;

            class Program
            {
                static void Main()
                {
                    Console.WriteLine("Hello.");
                }
            }
            """,
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("Test", [tree], RefAssemblyMetadata.All);
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot(TestContext.Current.CancellationToken);
        var formatter = new TreeFormatter();
        var formatted = formatter.Format(model, root).Text;
        output.WriteLine($"""
            ---
            {formatted}
            ---
            """);
        Assert.Equal("""
            TBD
            """, formatted);
    }
}
