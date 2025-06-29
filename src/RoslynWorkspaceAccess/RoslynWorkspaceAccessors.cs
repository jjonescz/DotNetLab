using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.ExternalAccess.Pythia.Api;
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
using Microsoft.CodeAnalysis.SignatureHelp;
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

    public static async Task<SignatureHelp?> GetSignatureHelpAsync(this Document document, int position, SignatureHelpTriggerReasonPublic reason, char? triggerCharacter, CancellationToken cancellationToken)
    {
        var signatureHelpService = document.Project.Solution.Services.ExportProvider.GetExports<SignatureHelpService>().Single().Value;
        var triggerInfo = new SignatureHelpTriggerInfo((SignatureHelpTriggerReason)reason, triggerCharacter);
        var (_, bestItems) = await signatureHelpService.GetSignatureHelpAsync(document, position, triggerInfo, cancellationToken);
        if (bestItems == null)
        {
            return null;
        }

        return new SignatureHelp
        {
            ActiveSignature = getActiveSignature(bestItems),
            ActiveParameter = bestItems.SemanticParameterIndex,
            Signatures = bestItems.Items.SelectAsArray(i => new SignatureInformation
            {
                Label = getSignatureText(i),
                Parameters = i.Parameters.SelectAsArray(static p => new ParameterInformation
                {
                    Label = p.Name,
                }),
                ActiveParameter = bestItems.SemanticParameterIndex,
            }),
        };

        static string getSignatureText(SignatureHelpItem item)
        {
            var sb = new StringBuilder();

            sb.Append(item.PrefixDisplayParts.GetFullText());

            var separators = item.SeparatorDisplayParts.GetFullText();
            for (var i = 0; i < item.Parameters.Length; i++)
            {
                var param = item.Parameters[i];

                if (i > 0)
                {
                    sb.Append(separators);
                }

                sb.Append(param.PrefixDisplayParts.GetFullText());
                sb.Append(param.DisplayParts.GetFullText());
                sb.Append(param.SuffixDisplayParts.GetFullText());
            }

            sb.Append(item.SuffixDisplayParts.GetFullText());
            sb.Append(item.DescriptionParts.GetFullText());

            return sb.ToString();
        }

        static int getActiveSignature(SignatureHelpItems items)
        {
            if (items.SelectedItemIndex.HasValue)
            {
                return items.SelectedItemIndex.Value;
            }

            var matchingSignature = items.Items.FirstOrDefault(
                sig => sig.Parameters.Length > items.SemanticParameterIndex);
            return matchingSignature != null ? items.Items.IndexOf(matchingSignature) : 0;
        }
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

[Export(typeof(IPythiaSignatureHelpProviderImplementation)), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
public sealed class NoOpPythiaSignatureHelpProviderImplementation() : IPythiaSignatureHelpProviderImplementation
{
    Task<(ImmutableArray<PythiaSignatureHelpItemWrapper> items, int? selectedItemIndex)> IPythiaSignatureHelpProviderImplementation.GetMethodGroupItemsAndSelectionAsync(ImmutableArray<IMethodSymbol> accessibleMethods, Document document, InvocationExpressionSyntax invocationExpression, SemanticModel semanticModel, SymbolInfo currentSymbol, CancellationToken cancellationToken)
    {
        return Task.FromResult<(ImmutableArray<PythiaSignatureHelpItemWrapper>, int?)>(([], null));
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

public enum SignatureHelpTriggerReasonPublic
{
    InvokeSignatureHelpCommand,
    TypeCharCommand,
    RetriggerCommand,
}
