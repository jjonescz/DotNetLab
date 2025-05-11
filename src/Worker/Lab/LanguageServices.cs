using BlazorMonaco;
using BlazorMonaco.Editor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace DotNetLab.Lab;

internal sealed class LanguageServices
{
    private readonly ILogger<LanguageServices> logger;
    private readonly AdhocWorkspace workspace;
    private readonly ProjectId projectId;
    private readonly ConditionalWeakTable<DocumentId, string> modelUris = new();
    private DocumentId? documentId;

    public LanguageServices(ILogger<LanguageServices> logger)
    {
        this.logger = logger;

        if (logger.IsEnabled(LogLevel.Trace))
        {
            RoslynWorkspaceAccessors.SetLogger(message => logger.LogTrace("Roslyn: {Message}", message));
        }

        workspace = new();
        var project = workspace
            .AddProject("TestProject", LanguageNames.CSharp)
            .AddMetadataReferences(RefAssemblyMetadata.All)
            .WithParseOptions(Compiler.CreateDefaultParseOptions())
            .WithCompilationOptions(Compiler.CreateDefaultCompilationOptions(Compiler.GetDefaultOutputKind([])));
        ApplyChanges(project.Solution);
        projectId = project.Id;
    }

    private Project Project => workspace.CurrentSolution.GetProject(projectId)!;

    public async Task<MonacoCompletionList> ProvideCompletionItemsAsync(string modelUri, Position position, MonacoCompletionContext context)
    {
        if (documentId == null || Project.GetDocument(documentId) is not { } document)
        {
            return new() { Suggestions = [] };
        }

        var text = await document.GetTextAsync();
        int caretPosition = text.Lines.GetPosition(position.ToLinePosition());
        var service = CompletionService.GetService(document)!;
        var completions = await service.GetCompletionsAsync(document, caretPosition);
        return completions.ToCompletionList(text.Lines);
    }

    public async Task OnDidChangeWorkspaceAsync(ImmutableArray<ModelInfo> models)
    {
        var modelLookupByUri = models.ToDictionary(m => m.Uri);

        // Make sure our workspaces matches `models`.
        foreach (Document doc in Project.Documents)
        {
            if (modelUris.TryGetValue(doc.Id, out string? modelUri))
            {
                // We have URI of this document, it's in our workspace.

                if (modelLookupByUri.TryGetValue(modelUri, out ModelInfo? model))
                {
                    // The document is still present in `models`.

                    if (doc.Name != model.FileName)
                    {
                        // Document has been renamed.
                        modelUris.Remove(doc.Id);

                        if (IsCSharp(fileName: model.FileName))
                        {
                            modelUris.Add(doc.Id, model.Uri);
                            ApplyChanges(workspace.CurrentSolution.WithDocumentFilePath(doc.Id, model.FileName));
                        }
                        else
                        {
                            ApplyChanges(Project.RemoveDocument(doc.Id).Solution);
                        }
                    }
                }
                else
                {
                    // Document has been removed from `models`.
                    modelUris.Remove(doc.Id);
                    ApplyChanges(Project.RemoveDocument(doc.Id).Solution);
                }

                // Mark this model URI as processed.
                modelLookupByUri.Remove(modelUri);
            }
            else
            {
                // We don't have URI of this document, it's not in our workspace, nothing to do.
            }
        }

        // Add new documents.
        foreach (var model in modelLookupByUri.Values)
        {
            if (IsCSharp(fileName: model.FileName))
            {
                var doc = Project.AddDocument(model.FileName, model.NewContent ?? string.Empty);
                modelUris.Add(doc.Id, model.Uri);
                ApplyChanges(doc.Project.Solution);
            }
        }

        // Update the current document.
        if (documentId != null && Project.GetDocument(documentId) is { } document)
        {
            if (modelUris.TryGetValue(document.Id, out string? modelUri))
            {
                OnDidChangeModel(modelUri: modelUri);
            }
            else
            {
                document = null;
            }
        }

        await UpdateOptionsIfNecessaryAsync();
    }

    private async Task UpdateOptionsIfNecessaryAsync()
    {
        var sources = await Project.Documents.SelectNonNullAsync(d => d.GetSyntaxTreeAsync());
        var outputKind = Compiler.GetDefaultOutputKind(sources);
        if (Project.CompilationOptions is { } options && outputKind != options.OutputKind)
        {
            ApplyChanges(Project.WithCompilationOptions(options.WithOutputKind(outputKind)).Solution);
        }
    }

    public void OnDidChangeModel(string modelUri)
    {
        // We are editing a different document now.
        documentId = modelUris.FirstOrDefault(kvp => kvp.Value == modelUri).Key;
    }

    public async Task OnDidChangeModelContentAsync(ModelContentChangedEvent args)
    {
        if (documentId == null || Project.GetDocument(documentId) is not { } document)
        {
            return;
        }

        var text = await document.GetTextAsync();
        text = text.WithChanges(args.Changes.ToTextChanges());
        document = document.WithText(text);
        ApplyChanges(document.Project.Solution);
        await UpdateOptionsIfNecessaryAsync();
    }

    public async Task<ImmutableArray<MarkerData>> GetDiagnosticsAsync()
    {
        if (documentId == null || Project.GetDocument(documentId) is not { } document)
        {
            return [];
        }

        var comp = await document.Project.GetCompilationAsync();
        if (comp == null)
        {
            return [];
        }

        var diagnostics = comp.GetDiagnostics().Where(d => d.Severity > DiagnosticSeverity.Hidden);
        return diagnostics.Select(d => d.ToMarkerData()).ToImmutableArray();
    }

    private void ApplyChanges(Solution solution)
    {
        if (!workspace.TryApplyChanges(solution))
        {
            logger.LogWarning("Failed to apply changes to the workspace.");
        }
    }

    private static bool IsCSharp(string fileName)
    {
        return fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }
}
