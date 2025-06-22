using BlazorMonaco;
using BlazorMonaco.Editor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotNetLab.Lab;

internal sealed class LanguageServices
{
    private readonly ILogger<LanguageServices> logger;
    private readonly AdhocWorkspace workspace;
    private readonly ProjectId projectId;
    private readonly ConditionalWeakTable<DocumentId, string> modelUris = new();
    private DocumentId? documentId;
    private CompletionList? lastCompletions;

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

    /// <returns>
    /// JSON-serialized <see cref="MonacoCompletionList"/>.
    /// We serialize here to avoid serializing twice unnecessarily
    /// (first on Worker to App interface, then on App to Monaco interface).
    /// </returns>
    public async Task<string> ProvideCompletionItemsAsync(string modelUri, Position position, MonacoCompletionContext context)
    {
        if (documentId == null || Project.GetDocument(documentId) is not { } document)
        {
            return """{"suggestions":[]}""";
        }

        var sw = Stopwatch.StartNew();
        var text = await document.GetTextAsync();
        int caretPosition = text.Lines.GetPosition(position.ToLinePosition());
        var service = CompletionService.GetService(document)!;

        var completionTrigger = context switch
        {
            { TriggerKind: MonacoCompletionTriggerKind.TriggerCharacter, TriggerCharacter: [.., var c] } => RoslynCompletionTrigger.CreateInsertionTrigger(c),
            _ => RoslynCompletionTrigger.Invoke,
        };

        bool shouldTriggerCompletion = service.ShouldTriggerCompletion(text, caretPosition, completionTrigger);
        if (completionTrigger.Kind is RoslynCompletionTriggerKind.Insertion
            && !shouldTriggerCompletion)
        {
            sw.Stop();
            logger.LogDebug("Determined completions should not trigger in {Milliseconds} ms", sw.ElapsedMilliseconds);
            return """{"suggestions":[]}""";
        }

        var completions = await service.GetCompletionsAsync(document, caretPosition, trigger: completionTrigger);
        lastCompletions = completions;
        var time1 = sw.ElapsedMilliseconds;
        sw.Restart();
        var result = completions.ToCompletionList(text);
        var time2 = sw.ElapsedMilliseconds;
        sw.Stop();
        logger.LogDebug("Got completions ({Count}) in {Milliseconds1} + {Milliseconds2} ms", completions.ItemsList.Count, time1, time2);
        return JsonSerializer.Serialize(result, BlazorMonacoJsonContext.Default.MonacoCompletionList);
    }

    /// <returns>
    /// JSON-serialized <see cref="MonacoCompletionItem"/>.
    /// We serialize here to avoid serializing twice unnecessarily
    /// (first on Worker to App interface, then on App to Monaco interface).
    /// </returns>
    /// <remarks>
    /// For inspiration, see <see href="https://github.com/dotnet/roslyn/blob/7c625024a1984d9f04f317940d518402f5898758/src/LanguageServer/Protocol/Handler/Completion/CompletionResultFactory.cs#L565"/>.
    /// </remarks>
    public async Task<string?> ResolveCompletionItemAsync(MonacoCompletionItem item)
    {
        // Try to find the corresponding Roslyn item.
        if (documentId == null ||
            lastCompletions == null ||
            lastCompletions.ItemsList.TryAt(item.Index) is not { } foundItem ||
            Project.GetDocument(documentId) is not { } document)
        {
            return null;
        }

        // Fill documentation.
        var service = CompletionService.GetService(document)!;
        var description = await service.GetDescriptionAsync(document, foundItem);
        item.Documentation = description?.Text;

        // Fill complex edits (e.g., snippet like `prop` or a type which needs `using` added).
        if (foundItem.IsComplexTextEdit)
        {
            item.AdditionalTextEdits = new();
            var completionChange = await service.GetChangeAsync(document, foundItem);
            var text = await document.GetTextAsync();
            foreach (var change in completionChange.TextChanges)
            {
                // Complex edits have InsertionText="". So whatever user typed (e.g., `prop`) will be removed (replaced with the empty insertion text).
                // If this edit is for the same span, it would get truncated, so we change the span to be before the typed text.
                var realSpan = change.Span.Start == foundItem.Span.Start ? new TextSpan(foundItem.Span.Start, 0) : change.Span;
                item.AdditionalTextEdits.Add(new()
                {
                    Text = change.NewText,
                    Range = text.Lines.GetLinePositionSpan(realSpan).ToRange(),
                });
            }
        }

        // Fill inline description.
        if (!string.IsNullOrEmpty(foundItem.InlineDescription))
        {
            item.Detail = foundItem.InlineDescription;
        }

        return JsonSerializer.Serialize(item, BlazorMonacoJsonContext.Default.MonacoCompletionItem);
    }

    /// <returns>
    /// Base64-encoded data (int32 array), see <see href="https://code.visualstudio.com/api/references/vscode-api#SemanticTokens"/>.
    /// </returns>
    /// <remarks>
    /// For inspiration, see <see href="https://github.com/dotnet/vscode-csharp/blob/4a83d86909df71ce209b3945e3f4696132cd3d45/src/omnisharp/features/semanticTokensProvider.ts#L161"/>
    /// and <see href="https://github.com/dotnet/roslyn/blob/7c625024a1984d9f04f317940d518402f5898758/src/LanguageServer/Protocol/Handler/SemanticTokens/SemanticTokensHelpers.cs#L22"/>.
    /// </remarks>
    public async Task<string?> ProvideSemanticTokensAsync(string modelUri, string? rangeJson, bool debug, CancellationToken cancellationToken = default)
    {
        if (documentId == null || Project.GetDocument(documentId) is not { } document)
        {
            return string.Empty;
        }

        var sw = Stopwatch.StartNew();
        var text = await document.GetTextAsync(cancellationToken);
        var lines = text.Lines;
        var range = rangeJson is null ? null : JsonSerializer.Deserialize(rangeJson, BlazorMonacoJsonContext.Default.Range);
        var span = range is null ? text.FullRange : range.ToSpan(lines);
        var classifiedSpansMutable = (await Classifier.GetClassifiedSpansAsync(document, span, cancellationToken)).AsList();

        classifiedSpansMutable.Sort(ClassifiedSpanComparer.Instance);

        var classifiedSpans = (IReadOnlyList<ClassifiedSpan>)classifiedSpansMutable;

        // Monaco Editor doesn't support multiline and overlapping spans.
        classifiedSpans = Classifier.ConvertMultiLineToSingleLineSpans(text, classifiedSpans);

        List<string>? converted = null;
        if (debug)
        {
            logger.LogDebug("Classified spans: {Spans}", classifiedSpans
                .Select(s => $"{s.ClassificationType}[{text.ToString(s.TextSpan)}]")
                .JoinToString(", "));
            converted = new(classifiedSpans.Count);
        }

        // Convert to the data format documented at https://code.visualstudio.com/api/references/vscode-api#DocumentSemanticTokensProvider.provideDocumentSemanticTokens.
        var data = new List<int>(classifiedSpans.Count * 5);

        int lastLineNumber = 0;
        int lastStartCharacter = 0;

        for (var currentClassifiedSpanIndex = 0; currentClassifiedSpanIndex < classifiedSpans.Count;)
        {
            var classifiedSpan = classifiedSpans[currentClassifiedSpanIndex];

            var originalTextSpan = classifiedSpan.TextSpan;
            var linePosition = lines.GetLinePosition(originalTextSpan.Start);

            var lineNumber = linePosition.Line;
            var deltaLine = lineNumber - lastLineNumber;
            lastLineNumber = lineNumber;

            var startCharacter = linePosition.Character;
            var deltaStartCharacter = startCharacter;
            if (deltaLine == 0)
            {
                deltaStartCharacter -= lastStartCharacter;
            }
            lastStartCharacter = startCharacter;

            var tokenType = -1;
            var tokenModifiers = 0;

            // Classified spans with the same text span should be combined into one token.
            do
            {
                if (SemanticTokensUtil.TokenModifiers.RoslynToLspIndexMap.TryGetValue(classifiedSpan.ClassificationType, out var m))
                {
                    tokenModifiers |= m;
                }
                else if (SemanticTokensUtil.TokenTypes.RoslynToLspIndexMap.TryGetValue(classifiedSpan.ClassificationType, out var t))
                {
                    tokenType = t;
                }
            }
            while (++currentClassifiedSpanIndex < classifiedSpans.Count &&
                (classifiedSpan = classifiedSpans[currentClassifiedSpanIndex]).TextSpan == originalTextSpan);

            if (tokenType < 0)
            {
                continue;
            }

            converted?.Add($"{SemanticTokensUtil.TokenTypes.LspValues[tokenType]}[{text.ToString(originalTextSpan)}]");

            data.Add(deltaLine);
            data.Add(deltaStartCharacter);
            data.Add(originalTextSpan.Length);
            data.Add(tokenType);
            data.Add(tokenModifiers);
        }

        if (converted != null)
        {
            logger.LogDebug("Converted semantic tokens: {Tokens}", converted.JoinToString(", "));
        }

        string result = Convert.ToBase64String(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(data)));

        sw.Stop();
        logger.LogDebug("Found {Count} semantic tokens for {Range} in {Milliseconds} ms", data.Count / 5, span, sw.ElapsedMilliseconds);

        return result;
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

                        if (model.FileName.IsCSharpFileName())
                        {
                            modelUris.Add(doc.Id, model.Uri);
                            ApplyChanges(workspace.CurrentSolution.WithDocumentFilePath(doc.Id, model.FileName));

                            if (model.NewContent != null)
                            {
                                // The caller sets the content to reset the state.
                                ApplyChanges(doc.WithText(SourceText.From(model.NewContent)).Project.Solution);
                            }
                        }
                        else
                        {
                            ApplyChanges(Project.RemoveDocument(doc.Id).Solution);
                        }
                    }
                    else if (model.NewContent != null)
                    {
                        // The caller sets the content to reset the state.
                        ApplyChanges(doc.WithText(SourceText.From(model.NewContent)).Project.Solution);
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
            if (model.FileName.IsCSharpFileName())
            {
                var doc = Project.AddDocument(
                    name: model.FileName,
                    text: model.NewContent ?? string.Empty,
                    filePath: model.FileName);
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
                documentId = null;
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

        // There might be changes that overlap, e.g., in an empty document,
        // write `Console` which also adds `using System;` and both these changes are at span [0..0).
        // Applying these changes sequentially solves the problem.
        foreach (var change in args.Changes.ToTextChanges())
        {
            text = text.WithChanges(change);
        }

        document = document.WithText(text);

        ApplyChanges(document.Project.Solution);
        await UpdateOptionsIfNecessaryAsync();
    }

    public async Task<ImmutableArray<MarkerData>> GetDiagnosticsAsync()
    {
        if (documentId == null ||
            Project.GetDocument(documentId) is not { } document ||
            !document.TryGetSyntaxTree(out var tree))
        {
            return [];
        }

        var comp = await document.Project.GetCompilationAsync();
        if (comp == null)
        {
            return [];
        }

        var diagnostics = comp.GetDiagnostics()
            .Where(d => d.Severity > DiagnosticSeverity.Hidden && d.Location.SourceTree == tree);
        return diagnostics.Select(static d => d.ToMarkerData()).ToImmutableArray();
    }

    private void ApplyChanges(Solution solution)
    {
        if (!workspace.TryApplyChanges(solution))
        {
            logger.LogWarning("Failed to apply changes to the workspace.");
        }
    }
}
