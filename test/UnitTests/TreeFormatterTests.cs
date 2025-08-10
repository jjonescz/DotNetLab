using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab;

public sealed class TreeFormatterTests(ITestOutputHelper output)
{
    [Fact]
    public void SyntaxTree()
    {
        var tree = CSharpSyntaxTree.ParseText("""
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
        var root = tree.GetRoot(TestContext.Current.CancellationToken);
        var formatter = WorkerServices.CreateTest().GetRequiredService<TreeFormatter>();
        var formatted = formatter.Format(root).Text;
        output.WriteLine($"""
            ---
            {formatted}
            ---
            """);
        Assert.Equal("""
            TBD
            """, formatted);
    }

    [Fact]
    public void Operation()
    {
        var tree = CSharpSyntaxTree.ParseText("""
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
        var root = tree.GetRoot(TestContext.Current.CancellationToken);
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var compilation = CSharpCompilation.Create("Test", [tree], RefAssemblyMetadata.All);
        var model = compilation.GetSemanticModel(tree);
        var operation = model.GetOperation(method, TestContext.Current.CancellationToken);
        var formatter = WorkerServices.CreateTest().GetRequiredService<TreeFormatter>();
        var formatted = formatter.Format(operation).Text;
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
