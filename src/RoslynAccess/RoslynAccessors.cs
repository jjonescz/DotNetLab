﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Diagnostics.CSharp;
using Microsoft.CodeAnalysis.Test.Utilities;

namespace DotNetLab;

public static class RoslynAccessors
{
    public static DiagnosticAnalyzer GetCSharpCompilerDiagnosticAnalyzer()
    {
        return new CSharpCompilerDiagnosticAnalyzer();
    }

    public static string GetDiagnosticsText(this IEnumerable<Diagnostic> actual, bool excludeFileName = false)
    {
        var sb = new StringBuilder();
        var e = actual.GetEnumerator();
        for (int i = 0; e.MoveNext(); i++)
        {
            Diagnostic d = e.Current;
            ReadOnlySpan<char> message = ((IFormattable)d).ToString(null, CultureInfo.InvariantCulture);

            // Remove file name to resemble Roslyn test output.
            var l = d.Location;
            if (excludeFileName && l.IsInSource)
            {
                var parenIndex = message.IndexOf('(');
                if (parenIndex > 0)
                {
                    message = message[parenIndex..];
                }
            }

            if (i > 0)
            {
                sb.AppendLine(",");
            }

            sb.Append("// ");
            sb.Append(message);
            sb.AppendLine();
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
