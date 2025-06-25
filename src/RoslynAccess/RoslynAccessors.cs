using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Diagnostics.CSharp;
using Microsoft.CodeAnalysis.Test.Utilities;

namespace DotNetLab;

file enum TriviaKind
{
    Leading,
    Trailing,
}

public static class RoslynAccessors
{
    public static string Dump(this CSharpSyntaxNode node)
    {
        return node.Dump();
    }

    public static string DumpExtended(this CSharpSyntaxNode node)
    {
        return TreeDumper.DumpCompact(nodeOrTokenToTree(node));

        static TreeDumperNode nodeOrTokenToTree(SyntaxNodeOrToken nodeOrToken)
        {
            string text = nodeOrToken.Kind().ToString();

            if (nodeOrToken.AsNode(out var node))
            {
                return new TreeDumperNode(text, null, node.ChildNodesAndTokens().Select(nodeOrTokenToTree));
            }

            return new TreeDumperNode(text + " " + stringOrMissing(nodeOrToken), null,
                [
                    ..triviaNode(TriviaKind.Leading, nodeOrToken.GetLeadingTrivia()),
                    ..triviaNode(TriviaKind.Trailing, nodeOrToken.GetTrailingTrivia()),
                ]);
        }

        static IEnumerable<TreeDumperNode> triviaNode(TriviaKind kind, SyntaxTriviaList triviaList)
        {
            if (!triviaList.Any())
            {
                return [];
            }

            var text = kind switch
            {
                TriviaKind.Leading => "LeadingTrivia",
                TriviaKind.Trailing => "TrailingTrivia",
                _ => throw new ArgumentOutOfRangeException(paramName: nameof(kind), message: kind.ToString()),
            };

            return [new TreeDumperNode(text, null, triviaList.Select(triviaToTree))];
        }

        static TreeDumperNode triviaToTree(SyntaxTrivia trivia)
        {
            return new TreeDumperNode($"""
                {trivia.Kind()} "{withoutNewLines(trivia.ToString())}"
                """, null,
                trivia.GetStructure() is { } structure ? [nodeOrTokenToTree(structure)] : []);
        }

        static string stringOrMissing(SyntaxNodeOrToken nodeOrToken)
        {
            if (!nodeOrToken.IsMissing)
            {
                return $"""
                    "{withoutNewLines(nodeOrToken.ToString())}"
                    """;
            }

            return "<missing>";
        }

        static string withoutNewLines(string s)
        {
            return s.ReplaceLineEndings("⏎");
        }
    }

    public static DiagnosticAnalyzer GetCSharpCompilerDiagnosticAnalyzer()
    {
        return new CSharpCompilerDiagnosticAnalyzer();
    }

    public static string GetDiagnosticsText(this IEnumerable<Diagnostic> actual)
    {
        var sb = new StringBuilder();
        var e = actual.GetEnumerator();
        for (int i = 0; e.MoveNext(); i++)
        {
            Diagnostic d = e.Current;
            string message = ((IFormattable)d).ToString(null, CultureInfo.InvariantCulture);

            if (i > 0)
            {
                sb.AppendLine(",");
            }

            sb.Append("// ");
            sb.AppendLine(message);
            var l = d.Location;
            if (l.IsInSource)
            {
                sb.Append("// ");
                sb.AppendLine(l.SourceTree.GetText().Lines.GetLineFromPosition(l.SourceSpan.Start).ToString());
            }

            var description = new DiagnosticDescription(d, errorCodeOnly: false);
            sb.Append(description.ToString());
        }
        return sb.ToString();
    }
}
