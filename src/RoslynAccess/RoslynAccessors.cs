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
    public static Type AssemblySymbolArrayType => typeof(ImmutableArray<AssemblySymbol>);
    public static Type InternalSymbolType => typeof(ISymbolInternal);

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

    extension(CSharpCompilationOptions options)
    {
        public CSharpCompilationOptions WithIgnoreAccessibility()
        {
            return options.WithTopLevelBinderFlags(options.TopLevelBinderFlags | BinderFlags.IgnoreAccessibility);
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

    public static string GetDiagnosticsText(
        this IEnumerable<Diagnostic> actual,
        bool excludeSingleFileName = false,
        bool roslynTestFormat = false)
    {
        excludeSingleFileName = excludeSingleFileName && hasSingleFileName(actual);
        var sb = new StringBuilder();
        using var e = actual.GetEnumerator();
        for (int i = 0; e.MoveNext(); i++)
        {
            Diagnostic d = e.Current;
            ReadOnlySpan<char> message = ((IFormattable)d).ToString(null, CultureInfo.InvariantCulture);

            // The file name is redundant when all diagnostics belong to the same file.
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

            if (roslynTestFormat)
            {
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
            else
            {
                if (i > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine();
                }

                sb.Append(message);

                if (l.IsInSource)
                {
                    var line = l.SourceTree.GetText().Lines.GetLineFromPosition(l.SourceSpan.Start);

                    sb.AppendLine();
                    sb.Append("    ");
                    sb.Append(line);
                }
            }
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

    public static bool TryGetBoundNodeSyntax(object o, [NotNullWhen(returnValue: true)] out SyntaxNode? syntax)
    {
        if (o is BoundNode boundNode)
        {
            syntax = boundNode.Syntax;
            return true;
        }

        syntax = null;
        return false;
    }

    public static bool TryGetInternalSymbolEquality(object? x, object? y, out bool areEqual)
    {
        if (x is ISymbolInternal xSymbol && y is ISymbolInternal ySymbol)
        {
            areEqual = xSymbol.Equals(ySymbol);
            return true;
        }

        areEqual = false;
        return false;
    }

    public static bool TryGetInternalSymbolHashCode(object o, out int hashCode)
    {
        if (o is ISymbolInternal symbol)
        {
            hashCode = symbol.GetHashCode();
            return true;
        }

        hashCode = 0;
        return false;
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

    public static bool TryGetInternalSymbolSyntax(object o, [NotNullWhen(returnValue: true)] out SyntaxNode? syntax)
    {
        if (o is Symbol { DeclaringSyntaxReferences: [{ } syntaxRef, ..] })
        {
            syntax = syntaxRef.GetSyntax();
            return true;
        }

        syntax = null;
        return false;
    }

    public static Func<object>? TryGetSourceInternalSymbolGetMembers(object o)
    {
        if (o is NamespaceOrTypeSymbol s && s.DeclaringCompilation != null)
        {
            return s.GetMembersAsObject;
        }

        return null;
    }

    internal static object GetMembersAsObject(this NamespaceOrTypeSymbol s) => s.GetMembers();

    public static bool TryGetUnderlyingSymbol(object o, [NotNullWhen(returnValue: true)] out object? symbol)
    {
        if (o is Microsoft.CodeAnalysis.CSharp.Symbols.PublicModel.Symbol publicSymbol)
        {
            symbol = publicSymbol.UnderlyingSymbol;
            return true;
        }

        symbol = null;
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
