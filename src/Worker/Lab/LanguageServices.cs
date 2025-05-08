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
    private Document? document;

    public LanguageServices(ILogger<LanguageServices> logger)
    {
        this.logger = logger;
        workspace = new();
        var project = workspace
            .AddProject("TestProject", LanguageNames.CSharp)
            .AddMetadataReferences(RefAssemblyMetadata.All);
        ApplyChanges(project.Solution);
        projectId = project.Id;
    }

    private Project Project => workspace.CurrentSolution.GetProject(projectId)!;

    public async Task<MonacoCompletionList> ProvideCompletionItemsAsync(string modelUri, Position position, MonacoCompletionContext context)
    {
        if (document == null)
        {
            return new() { Suggestions = [] };
        }

        var text = await document.GetTextAsync();
        int caretPosition = text.Lines.GetPosition(position.ToLinePosition());
        var service = CompletionService.GetService(document)!;
        var completions = await service.GetCompletionsAsync(document, caretPosition);
        return completions.ToCompletionList(text.Lines);
    }

    public void OnDidChangeWorkspace(ImmutableArray<ModelInfo> models)
    {
        var modelLookupByUri = models.ToDictionary(m => m.Uri);

        // Make sure our workspaces matches `models`.
        foreach (Document document in Project.Documents)
        {
            if (modelUris.TryGetValue(document.Id, out string? modelUri))
            {
                // We have URI of this document, it's in our workspace.

                if (modelLookupByUri.TryGetValue(modelUri, out ModelInfo? model))
                {
                    // The document is still present in `models`.

                    if (document.Name != model.FileName)
                    {
                        // Document has been renamed.
                        modelUris.Remove(document.Id);

                        if (IsCSharp(fileName: model.FileName))
                        {
                            modelUris.Add(document.Id, model.Uri);
                            ApplyChanges(workspace.CurrentSolution.WithDocumentFilePath(document.Id, model.FileName));
                        }
                        else
                        {
                            ApplyChanges(Project.RemoveDocument(document.Id).Solution);
                        }
                    }
                }
                else
                {
                    // Document has been removed from `models`.
                    modelUris.Remove(document.Id);
                    ApplyChanges(Project.RemoveDocument(document.Id).Solution);
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
                var document = Project.AddDocument(model.FileName, model.NewContent ?? string.Empty);
                modelUris.Add(document.Id, model.Uri);
                ApplyChanges(document.Project.Solution);
            }
        }

        // Update the current document.
        if (document != null)
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
    }

    public void OnDidChangeModel(string modelUri)
    {
        // We are editing a different document now.
        DocumentId? documentId = modelUris.FirstOrDefault(kvp => kvp.Value == modelUri).Key;
        document = documentId == null ? null : Project.GetDocument(documentId);
    }

    public async Task OnDidChangeModelContentAsync(ModelContentChangedEvent args)
    {
        if (document == null)
        {
            return;
        }

        var text = await document.GetTextAsync();
        text = text.WithChanges(args.Changes.ToTextChanges());
        document = document.WithText(text);
        ApplyChanges(document.Project.Solution);
    }

    public async Task<ImmutableArray<MarkerData>> GetDiagnosticsAsync()
    {
        if (document == null)
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
