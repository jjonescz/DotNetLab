using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetLab;

public static class CodeAnalysisUtil
{
    public static bool TryGetHostOutputSafe(
        this GeneratorRunResult result,
        string key,
        [NotNullWhen(returnValue: true)] out object? value)
    {
        if (result.GetType().GetProperty("HostOutputs")?.GetValue(result) is ImmutableDictionary<string, object?> hostOutputs)
        {
            return hostOutputs.TryGetValue(key, out value);
        }

        value = null;
        return false;
    }

    public static DiagnosticData ToDiagnosticData(this Diagnostic d)
    {
        string? filePath = d.Location.SourceTree?.FilePath;
        FileLinePositionSpan lineSpan;

        if (string.IsNullOrEmpty(filePath) &&
            d.Location.GetMappedLineSpan() is { IsValid: true } mappedLineSpan)
        {
            filePath = mappedLineSpan.Path;
            lineSpan = mappedLineSpan;
        }
        else
        {
            lineSpan = d.Location.GetLineSpan();
        }

        return new DiagnosticData(
            FilePath: filePath,
            Severity: d.Severity switch
            {
                DiagnosticSeverity.Error => DiagnosticDataSeverity.Error,
                DiagnosticSeverity.Warning => DiagnosticDataSeverity.Warning,
                _ => DiagnosticDataSeverity.Info,
            },
            Id: d.Id,
            HelpLinkUri: d.Descriptor.HelpLinkUri,
            Message: d.GetMessage(),
            StartLineNumber: lineSpan.StartLinePosition.Line + 1,
            StartColumn: lineSpan.StartLinePosition.Character + 1,
            EndLineNumber: lineSpan.EndLinePosition.Line + 1,
            EndColumn: lineSpan.EndLinePosition.Character + 1
        );
    }
}

internal static class RazorUtil
{
    public static IReadOnlyList<RazorDiagnostic> GetDiagnostics(this RazorCSharpDocument document)
    {
        // Different razor versions return IReadOnlyList vs ImmutableArray,
        // so we need to use reflection to avoid MissingMethodException.
        return (IReadOnlyList<RazorDiagnostic>)document.GetType()
            .GetProperty(nameof(document.Diagnostics))!
            .GetValue(document)!;
    }

    public static string GetGeneratedCode(this RazorCSharpDocument document)
    {
        // There can be either `string GeneratedCode` or `SourceText Text` property.
        // See https://github.com/dotnet/razor/pull/11404.

        var documentType = document.GetType();
        var textProperty = documentType.GetProperty("Text");
        if (textProperty != null)
        {
            return ((SourceText)textProperty.GetValue(document)!).ToString();
        }

        return (string)documentType.GetProperty("GeneratedCode")!.GetValue(document)!;
    }

    public static IEnumerable<RazorProjectItem> EnumerateItemsSafe(this RazorProjectFileSystem fileSystem, string basePath)
    {
        // EnumerateItems was defined in RazorProject before https://github.com/dotnet/razor/pull/11379,
        // then it has moved into RazorProjectFileSystem. Hence we need reflection to access it.
        return (IEnumerable<RazorProjectItem>)fileSystem.GetType()
            .GetMethod(nameof(fileSystem.EnumerateItems))!
            .Invoke(fileSystem, [basePath])!;
    }

    public static RazorCodeDocument ProcessDeclarationOnlySafe(
        this RazorProjectEngine engine,
        RazorProjectItem projectItem)
    {
        return engine.ProcessSafe(projectItem, nameof(engine.ProcessDeclarationOnly));
    }

    public static RazorCodeDocument ProcessDesignTimeSafe(
        this RazorProjectEngine engine,
        RazorProjectItem projectItem)
    {
        return engine.ProcessSafe(projectItem, nameof(engine.ProcessDesignTime));
    }

    public static RazorCodeDocument ProcessSafe(
        this RazorProjectEngine engine,
        RazorProjectItem projectItem)
    {
        return engine.ProcessSafe(projectItem, nameof(engine.Process));
    }

    private static RazorCodeDocument ProcessSafe(
        this RazorProjectEngine engine,
        RazorProjectItem projectItem,
        string methodName)
    {
        // Newer razor versions take CancellationToken parameter,
        // so we need to use reflection to avoid MissingMethodException.

        var method = engine.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(m => m.Name == methodName &&
                m.GetParameters() is
                [
                { ParameterType.FullName: "Microsoft.AspNetCore.Razor.Language.RazorProjectItem" },
                    .. var rest
                ] &&
                rest.All(static p => p.IsOptional))
            .First();

        return (RazorCodeDocument)method
            .Invoke(engine, [projectItem, ..Enumerable.Repeat<object?>(null, method.GetParameters().Length - 1)])!;
    }

    public static Diagnostic ToDiagnostic(this RazorDiagnostic d)
    {
        DiagnosticSeverity severity = d.Severity.ToDiagnosticSeverity();

        string message = d.GetMessage();

        var descriptor = new DiagnosticDescriptor(
            id: d.Id,
            title: message,
            messageFormat: message,
            category: "Razor",
            defaultSeverity: severity,
            isEnabledByDefault: true);

        return Diagnostic.Create(
            descriptor,
            location: d.Span.ToLocation());
    }

    public static DiagnosticSeverity ToDiagnosticSeverity(this RazorDiagnosticSeverity severity)
    {
        return severity switch
        {
            RazorDiagnosticSeverity.Error => DiagnosticSeverity.Error,
            RazorDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Info,
        };
    }

    public static Location ToLocation(this SourceSpan span)
    {
        if (span == SourceSpan.Undefined)
        {
            return Location.None;
        }

        return Location.Create(
            filePath: span.FilePath,
            textSpan: span.ToTextSpan(),
            lineSpan: span.ToLinePositionSpan());
    }

    public static LinePositionSpan ToLinePositionSpan(this SourceSpan span)
    {
        var lineCount = span.LineCount < 1 ? 1 : span.LineCount;
        return new LinePositionSpan(
            start: new LinePosition(
                line: span.LineIndex,
                character: span.CharacterIndex),
            end: new LinePosition(
                line: span.LineIndex + lineCount - 1,
                character: span.CharacterIndex + span.Length));
    }

    public static TextSpan ToTextSpan(this SourceSpan span)
    {
        return new TextSpan(span.AbsoluteIndex, span.Length);
    }
}

internal readonly record struct RazorGeneratorResultSafe(object Inner)
{
    public bool TryGetCodeDocument(
        string physicalPath,
        [NotNullWhen(returnValue: true)] out RazorCodeDocument? result)
    {
        var method = Inner.GetType().GetMethod("GetCodeDocument");
        if (method is not null &&
            method.GetParameters() is [{ } param] &&
            param.ParameterType == typeof(string) &&
            method.Invoke(Inner, [physicalPath]) is RazorCodeDocument innerResult)
        {
            result = innerResult;
            return true;
        }

        result = null;
        return false;
    }
}
