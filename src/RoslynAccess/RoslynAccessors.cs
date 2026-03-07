using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Diagnostics.CSharp;
using Microsoft.CodeAnalysis.Symbols;
using Microsoft.CodeAnalysis.Test.Utilities;

namespace DotNetLab;

public static class RoslynAccessors
{
    extension(Compilation compilation)
    {
        public bool GenerateDocumentationCommentsInternal(
            Stream? xmlDocStream,
            string? outputNameOverride,
            out ImmutableArray<Diagnostic> diagnostics,
            CancellationToken cancellationToken)
        {
            var diagnosticBag = DiagnosticBag.GetInstance();
            var result = compilation.GenerateDocumentationComments(
                xmlDocStream,
                outputNameOverride,
                diagnosticBag,
                cancellationToken);
            diagnostics = diagnosticBag.ToReadOnlyAndFree();
            return result;
        }
    }

    extension(SemanticModel semanticModel)
    {
        public SemanticModel? GetMemberSemanticModel(SyntaxNode? node)
        {
            if (node is null) return null;
            return ((CSharpSemanticModel)semanticModel).GetMemberModel(node);
        }

        public object GetBoundRoot()
        {
            return ((MemberSemanticModel)semanticModel).GetBoundRoot();
        }
    }

    public static DiagnosticAnalyzer GetCSharpCompilerDiagnosticAnalyzer()
    {
        return new CSharpCompilerDiagnosticAnalyzer();
    }

    public static string GetDiagnosticsText(this IEnumerable<Diagnostic> actual, bool excludeSingleFileName = false)
    {
        excludeSingleFileName = excludeSingleFileName && hasSingleFileName(actual);
        var sb = new StringBuilder();
        using var e = actual.GetEnumerator();
        for (int i = 0; e.MoveNext(); i++)
        {
            Diagnostic d = e.Current;
            ReadOnlySpan<char> message = ((IFormattable)d).ToString(null, CultureInfo.InvariantCulture);

            // Remove file name to resemble Roslyn test output.
            var l = d.Location;
            if (excludeSingleFileName)
            {
                var colonIndex = message.IndexOf(':');
                if (colonIndex > 0)
                {
                    var parenIndex = message[..colonIndex].IndexOf('(');
                    if (parenIndex > 0)
                    {
                        message = message[parenIndex..];
                    }
                }
            }

            if (i > 0)
            {
                sb.AppendLine(",");
            }

            foreach (var messageLine in message.EnumerateLines())
            {
                sb.Append("// ");
                sb.Append(messageLine);
                sb.AppendLine();
            }

            if (l.IsInSource)
            {
                sb.Append("// ");
                sb.AppendLine(l.SourceTree.GetText().Lines.GetLineFromPosition(l.SourceSpan.Start).ToString());
            }

            var description = new DiagnosticDescription(d, errorCodeOnly: false);
            sb.Append(description.ToString());
        }

        return sb.ToString();

        static bool hasSingleFileName(IEnumerable<Diagnostic> diagnostics)
        {
            string? fileName = null;
            foreach (var d in diagnostics)
            {
                var l = d.Location;
                if (l.IsInSource)
                {
                    var currentFileName = l.GetMappedLineSpan() is { IsValid: true } mapped
                        ? mapped.Path
                        : l.SourceTree.FilePath;

                    if (currentFileName == null)
                    {
                        return false;
                    }

                    if (fileName == null)
                    {
                        fileName = currentFileName;
                    }
                    else if (fileName != currentFileName)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    public static IEnumerable<string> GetFeatureNames()
    {
        return typeof(Feature)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);
    }

    public static bool TryGetInternalSymbolName(object o, [NotNullWhen(returnValue: true)] out string? name)
    {
        if (o is ISymbolInternal { Name: { } symbolName })
        {
            name = symbolName;
            return true;
        }

        name = null;
        return false;
    }
}

public sealed class RefSafetyAnalysisAccessor
{
    private static readonly ConstructorInfo? ctor;
    private static readonly MethodInfo? inUnsafeMethod;

    static RefSafetyAnalysisAccessor()
    {
        ctor = typeof(RefSafetyAnalysis).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            [
                typeof(CSharpCompilation),
                typeof(MethodSymbol),
                typeof(BoundNode),
                typeof(bool),
                typeof(bool),
                typeof(Microsoft.CodeAnalysis.CSharp.BindingDiagnosticBag),
            ]);
        if (ctor is null) return;

        inUnsafeMethod = typeof(RefSafetyAnalysis).GetMethod(
            "InUnsafeMethod",
            BindingFlags.Static | BindingFlags.NonPublic);
    }

    private readonly RefSafetyAnalysis refSafetyAnalysis;

    private RefSafetyAnalysisAccessor(RefSafetyAnalysis refSafetyAnalysis)
    {
        this.refSafetyAnalysis = refSafetyAnalysis;
    }

    public static RefSafetyAnalysisAccessor? TryCreate(Compilation compilation, SemanticModel memberSemanticModel)
    {
        if (ctor is null) return null;

        var memberSemanticModelCasted = (MemberSemanticModel)memberSemanticModel;

        var symbol = memberSemanticModelCasted.MemberSymbol as MethodSymbol;
        if (symbol is null) return null;

        var boundRoot = memberSemanticModelCasted.GetBoundRoot();

        var visitor = (RefSafetyAnalysis)ctor.Invoke([
            compilation,
            symbol,
            boundRoot,
            inUnsafeMethod?.Invoke(null, [symbol]) ?? false,
            symbol.ContainingModule.UseUpdatedEscapeRules,
            Microsoft.CodeAnalysis.CSharp.BindingDiagnosticBag.Discarded,
        ]);

        visitor.Visit(boundRoot);

        return new RefSafetyAnalysisAccessor(visitor);
    }

    public string? GetValEscape(object? input)
    {
        return input is BoundExpression node
            ? UnwrapSafeContext(refSafetyAnalysis.GetValEscape(node))
            : null;
    }

    public string? GetRefEscape(object? input)
    {
        return input is BoundExpression node
            ? UnwrapSafeContext(refSafetyAnalysis.GetRefEscape(node))
            : null;
    }

    private static string UnwrapSafeContext(SafeContext safeContext)
    {
        return safeContext.ToString();
    }
}
