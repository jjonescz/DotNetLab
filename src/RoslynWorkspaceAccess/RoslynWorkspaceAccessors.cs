using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.ExtractClass;
using Microsoft.CodeAnalysis.ExtractInterface;
using Microsoft.CodeAnalysis.GenerateType;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.Notification;
using Microsoft.CodeAnalysis.ProjectManagement;
using Microsoft.CodeAnalysis.Serialization;
using Microsoft.CodeAnalysis.Storage;
using Microsoft.CodeAnalysis.Text;
using System.Composition;

namespace DotNetLab;

public static class RoslynWorkspaceAccessors
{
    extension(TaggedText taggedText)
    {
        public TaggedTextStylePublic Style => (TaggedTextStylePublic)taggedText.Style;
        public string? NavigationHint => taggedText.NavigationHint;
        public string? NavigationTarget => taggedText.NavigationTarget;
    }

    public static async Task<IEnumerable<CodeAction>> GetCodeActionsAsync(this Document document, TextSpan span, CancellationToken cancellationToken)
    {
        var codeFixService = document.Project.Solution.Services.ExportProvider.GetExports<ICodeFixService>().Single().Value;
        var fixes = await codeFixService.GetFixesAsync(document, span, cancellationToken);
        return fixes.Where(static c => c.Fixes.Length != 0)
            .SelectMany(static c => c.Fixes.Select(static f => f.Action))
            .Where(isSupportedCodeAction);

        // https://github.com/dotnet/roslyn/blob/8aa0e2e2ccb66c8b0fe0e002d80a92e870304665/src/LanguageServer/Protocol/Handler/CodeActions/CodeActionHelpers.cs#L96
        static bool isSupportedCodeAction(CodeAction codeAction)
        {
            if ((codeAction is CodeActionWithOptions and not ExtractInterfaceCodeAction and not ExtractClassWithDialogCodeAction) ||
                codeAction.Tags.Contains(CodeAction.RequiresNonDocumentChange))
            {
                return false;
            }

            return true;
        }
    }

    public static DocumentTextDifferencingService GetDocumentTextDifferencingService(this SolutionServices services)
    {
        var service = services.GetRequiredService<IDocumentTextDifferencingService>();
        return new DocumentTextDifferencingService(service);
    }

    [SuppressMessage("Interoperability", "CA1416: Validate platform compatibility")]
    public static AnalyzerImageReference RegisterAnalyzer(this AnalyzerImageReference analyzer)
    {
        SerializerService.TestAccessor.AddAnalyzerImageReference(analyzer);
        return analyzer;
    }

    public static void SetLogger(Action<string> logger)
    {
        Logger.SetLogger(new RoslynLogger(logger));
    }

    public static Task<Solution> WithMergedLinkedFileChangesAsync(
        this Solution solution,
        Solution oldSolution,
        SolutionChanges? solutionChanges = null,
        CancellationToken cancellationToken = default)
    {
        return solution.WithMergedLinkedFileChangesAsync(oldSolution, solutionChanges, cancellationToken);
    }
}

internal sealed class RoslynLogger(Action<string> logger) : ILogger
{
    public bool IsEnabled(FunctionId functionId)
    {
        return true;
    }

    public void Log(FunctionId functionId, LogMessage logMessage)
    {
        logger($"{logMessage.LogLevel} {functionId} {logMessage.GetMessage()}");
    }

    public void LogBlockStart(FunctionId functionId, LogMessage logMessage, int uniquePairId, CancellationToken cancellationToken)
    {
        logger($"{logMessage.LogLevel} {functionId} start({uniquePairId}) {logMessage.GetMessage()}");
    }

    public void LogBlockEnd(FunctionId functionId, LogMessage logMessage, int uniquePairId, int delta, CancellationToken cancellationToken)
    {
        string suffix = cancellationToken.IsCancellationRequested ? " cancelled" : string.Empty;
        logger($"{logMessage.LogLevel} {functionId}{suffix} end({uniquePairId}, {delta}ms) {logMessage.GetMessage()}");
    }
}

public sealed class DocumentTextDifferencingService
{
    private readonly IDocumentTextDifferencingService inner;

    internal DocumentTextDifferencingService(IDocumentTextDifferencingService inner)
    {
        this.inner = inner;
    }

    public Task<ImmutableArray<TextChange>> GetTextChangesAsync(Document oldDocument, Document newDocument, CancellationToken cancellationToken)
        => inner.GetTextChangesAsync(oldDocument, newDocument, cancellationToken);
}

[ExportWorkspaceService(typeof(IPersistentStorageConfiguration), ServiceLayer.Test), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
public sealed class NoOpPersistentStorageConfiguration() : IPersistentStorageConfiguration
{
    public bool ThrowOnFailure => false;

    string? IPersistentStorageConfiguration.TryGetStorageLocation(SolutionKey solutionKey) => null;
}

[ExportWorkspaceService(typeof(IGenerateTypeOptionsService), ServiceLayer.Test), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
public sealed class NoOpGenerateTypeOptionsService() : IGenerateTypeOptionsService
{
    GenerateTypeOptionsResult IGenerateTypeOptionsService.GetGenerateTypeOptions(string className, GenerateTypeDialogOptions generateTypeDialogOptions, Document document, INotificationService? notificationService, IProjectManagementService? projectManagementService, ISyntaxFactsService syntaxFactsService)
    {
        return GenerateTypeOptionsResult.Cancelled;
    }
}

[Flags]
public enum TaggedTextStylePublic
{
    None = 0,
    Strong = 1 << 0,
    Emphasis = 1 << 1,
    Underline = 1 << 2,
    Code = 1 << 3,
    PreserveWhitespace = 1 << 4,
}
