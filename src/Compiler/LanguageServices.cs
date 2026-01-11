using BlazorMonaco;
using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DotNetLab;

internal sealed class LanguageServices : ILanguageServices
{
    private readonly ILogger<LanguageServices> logger;
    private readonly Compiler compiler;
    private readonly AsyncLock workspaceLock = new();
    private readonly AdhocWorkspace workspace;
    private readonly ProjectId projectId;

    /// <summary>
    /// Project containing the <see cref="CompilationInput.Configuration"/>.
    /// </summary>
    private readonly ProjectId configurationProjectId;

    private readonly ConditionalWeakTable<DocumentId, string> modelUris = new();
    private (DocumentId DocId, RoslynCompletionList List)? lastCompletions;
    private CompiledAssembly? compilerDiagnostics;
    private ImmutableArray<MetadataReference> additionalConfigurationReferences;
    private ImmutableArray<DocumentId> additionalSourceDocuments;
    private OutputKind defaultOutputKind = Compiler.GetDefaultOutputKind([]);

    public LanguageServices(
        ILogger<LanguageServices> logger,
        Compiler compiler)
    {
        this.logger = logger;
        this.compiler = compiler;

        if (logger.IsEnabled(LogLevel.Trace))
        {
            RoslynWorkspaceAccessors.SetLogger(message => logger.LogTrace("Roslyn: {Message}", message));
        }

        workspace = new(MefHostServices.Create(
            [
                .. MefHostServices.DefaultAssemblies,
                typeof(FileLevelDirectiveCompletionProvider).Assembly,
                typeof(RoslynWorkspaceAccessors).Assembly,
            ]));

        IEnumerable<AnalyzerReference> analyzerReferences =
        [
            // CompilerDiagnosticAnalyzer for CodeFixService (which only works on analyzer diagnostics).
            new AnalyzerImageReference([RoslynAccessors.GetCSharpCompilerDiagnosticAnalyzer()]).RegisterAnalyzer(),
            findRoslynAnalyzers(),
        ];

        projectId = createProject(configuration: false);
        configurationProjectId = createProject(configuration: true);

        ProjectId createProject(bool configuration)
        {
            string name = configuration ? "ConfigurationProject" : "TestProject";
            var compilationOptions = configuration
                ? Compiler.CreateConfigurationCompilationOptions()
                : Compiler.CreateDefaultCompilationOptions(defaultOutputKind);
            var project = workspace
                .AddProject(name, LanguageNames.CSharp)
                .AddMetadataReferences(RefAssemblyMetadata.All)
                .WithAnalyzerReferences(analyzerReferences)
                .WithParseOptions(Compiler.CreateDefaultParseOptions())
                .WithCompilationOptions(compilationOptions);

            if (configuration)
            {
                project = project.AddDocument("GlobalUsings.cs", Compiler.ConfigurationGlobalUsings).Project;
            }

            ApplyChanges(project.Solution);
            return project.Id;
        }

        static AnalyzerReference findRoslynAnalyzers()
        {
            var analyzers = RoslynCodeStyleAccessors.GetRoslynCodeStyleAssembly().GetTypes()
                .Where(static t =>
                    t.GetCustomAttributesData().Any(static a => a.AttributeType.FullName == "Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzerAttribute") &&
                    t.IsSubclassOf(typeof(DiagnosticAnalyzer)))
                .Select(static t => (DiagnosticAnalyzer)Activator.CreateInstance(t)!)
                .ToImmutableArray();
            return new AnalyzerImageReference(analyzers).RegisterAnalyzer();
        }
    }

    /// <returns>
    /// JSON-serialized <see cref="MonacoCompletionList"/>.
    /// We serialize here to avoid serializing twice unnecessarily
    /// (first on Worker to App interface, then on App to Monaco interface).
    /// </returns>
    public async Task<string> ProvideCompletionItemsAsync(string modelUri, Position position, MonacoCompletionContext context, CancellationToken cancellationToken)
    {
        if (!TryGetDocument(modelUri, out var document))
        {
            return """{"suggestions":[]}""";
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var text = await document.GetTextAsync(cancellationToken);
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
                logger.LogDebug("Determined completions should not trigger in {Milliseconds} ms", sw.ElapsedMilliseconds.SeparateThousands());
                return """{"suggestions":[]}""";
            }

            var completions = await service.GetCompletionsAsync(document, caretPosition, trigger: completionTrigger, cancellationToken: cancellationToken);
            lastCompletions = (document.Id, completions);
            var time1 = sw.ElapsedMilliseconds;
            sw.Restart();
            var result = completions.ToCompletionList(text);
            var time2 = sw.ElapsedMilliseconds;
            logger.LogDebug("Got completions ({Count}) for {Position} in {Milliseconds1} + {Milliseconds2} ms", completions.ItemsList.Count, position.Stringify(), time1.SeparateThousands(), time2.SeparateThousands());
            return JsonSerializer.Serialize(result, BlazorMonacoJsonContext.Default.MonacoCompletionList);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Canceled completions for {Position} in {Time} ms", position.Stringify(), sw.ElapsedMilliseconds.SeparateThousands());
            return """{"suggestions":[],"isIncomplete":true}""";
        }
    }

    /// <returns>
    /// JSON-serialized <see cref="MonacoCompletionItem"/>.
    /// We serialize here to avoid serializing twice unnecessarily
    /// (first on Worker to App interface, then on App to Monaco interface).
    /// </returns>
    /// <remarks>
    /// For inspiration, see <see href="https://github.com/dotnet/roslyn/blob/7c625024a1984d9f04f317940d518402f5898758/src/LanguageServer/Protocol/Handler/Completion/CompletionResultFactory.cs#L565"/>.
    /// </remarks>
    public async Task<string?> ResolveCompletionItemAsync(MonacoCompletionItem item, CancellationToken cancellationToken)
    {
        // Try to find the corresponding Roslyn item.
        if (lastCompletions is not (var docId, var lastCompletionList) ||
            lastCompletionList.ItemsList.TryAt(item.Index) is not { } foundItem ||
            GetDocument(docId) is not { } document)
        {
            return null;
        }

        try
        {
            // Fill documentation.
            var service = CompletionService.GetService(document)!;
            var description = await service.GetDescriptionAsync(document, foundItem, cancellationToken);
            item.Documentation = description?.Text;

            // Fill complex edits (e.g., snippet like `prop` or a type which needs `using` added).
            if (foundItem.IsComplexTextEdit)
            {
                item.AdditionalTextEdits = new();
                var completionChange = await service.GetChangeAsync(document, foundItem, cancellationToken: cancellationToken);
                var text = await document.GetTextAsync(cancellationToken);
                foreach (var change in completionChange.TextChanges)
                {
                    // If this edit is for the original span, it should be in InsertText to work correctly.
                    if (change.Span.Start == foundItem.Span.Start)
                    {
                        Debug.Assert(item.InsertText == "");
                        item.InsertText = change.NewText;
                    }
                    else
                    {
                        item.AdditionalTextEdits.Add(new()
                        {
                            Text = change.NewText,
                            Range = change.Span.ToRange(text.Lines),
                        });
                    }
                }
            }

            // Fill inline description.
            if (!string.IsNullOrEmpty(foundItem.InlineDescription))
            {
                item.Detail = foundItem.InlineDescription;
            }

            return JsonSerializer.Serialize(item, BlazorMonacoJsonContext.Default.MonacoCompletionItem);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <returns>
    /// Base64-encoded data (int32 array), see <see href="https://code.visualstudio.com/api/references/vscode-api#SemanticTokens"/>.
    /// </returns>
    /// <remarks>
    /// For inspiration, see <see href="https://github.com/dotnet/vscode-csharp/blob/4a83d86909df71ce209b3945e3f4696132cd3d45/src/omnisharp/features/semanticTokensProvider.ts#L161"/>
    /// and <see href="https://github.com/dotnet/roslyn/blob/7c625024a1984d9f04f317940d518402f5898758/src/LanguageServer/Protocol/Handler/SemanticTokens/SemanticTokensHelpers.cs#L22"/>.
    /// </remarks>
    public Task<string?> ProvideSemanticTokensAsync(string modelUri, string? rangeJson, bool debug, CancellationToken cancellationToken)
    {
        if (!TryGetDocument(modelUri, out var document))
        {
            return SemanticTokensUtil.EmptyResponse;
        }

        return ConvertSemanticTokensAsync(logger, docPath: $"in/{document.FilePath}", rangeJson: rangeJson, debug, async (range) =>
        {
            var text = await document.GetTextAsync(cancellationToken);
            var lines = text.Lines;
            var span = range is null ? text.FullRange : range.ToSpan(lines);
            var classifiedSpansMutable = (await Classifier.GetClassifiedSpansAsync(document, span, cancellationToken)).AsList();
            return (text, classifiedSpansMutable);
        });
    }

    public static async Task<string?> ConvertSemanticTokensAsync(ILogger logger, string? docPath, string? rangeJson, bool debug, Func<MonacoRange?, ValueTask<(SourceText, IList<ClassifiedSpan>)>> factory)
    {
        var sw = Stopwatch.StartNew();
        var range = rangeJson is null ? null : JsonSerializer.Deserialize(rangeJson, BlazorMonacoJsonContext.Default.Range);
        try
        {
            var (text, classifiedSpansMutable) = await factory(range);

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

            var bytes = Classifier.ConvertToLspFormat(text, classifiedSpans, converted);

            if (converted != null)
            {
                logger.LogDebug("Converted semantic tokens: {Tokens}", converted.JoinToString(", "));
            }

            string result = Convert.ToBase64String(bytes);

            logger.LogDebug("Got semantic tokens ({Bytes} bytes) for {DocPath}{Range} in {Milliseconds} ms", bytes.Length.SeparateThousands(), docPath, range.Stringify(), sw.ElapsedMilliseconds.SeparateThousands());

            return result;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Canceled completions for {DocPath}{Range} in {Time} ms", docPath, range.Stringify(), sw.ElapsedMilliseconds.SeparateThousands());

            return SemanticTokensUtil.CancelledResponse;
        }
    }

    /// <returns>
    /// JSON-serialized list of <see cref="MonacoCodeAction"/>s.
    /// We serialize here to avoid serializing twice unnecessarily
    /// (first on Worker to App interface, then on App to Monaco interface).
    /// </returns>
    /// <remarks>
    /// For inspiration, see <see href="https://github.com/dotnet/vscode-csharp/blob/4a83d86909df71ce209b3945e3f4696132cd3d45/src/omnisharp/features/codeActionProvider.ts"/>
    /// and <see href="https://github.com/dotnet/roslyn/blob/ad14335550de1134f0b5a59b6cd040001d0d8c8d/src/LanguageServer/Protocol/Handler/CodeActions/CodeActionHelpers.cs"/>
    /// </remarks>
    public async Task<string?> ProvideCodeActionsAsync(string modelUri, string? rangeJson, CancellationToken cancellationToken)
    {
        if (!TryGetDocument(modelUri, out var document))
        {
            return null;
        }

        var sw = Stopwatch.StartNew();
        var range = rangeJson is null ? null : JsonSerializer.Deserialize(rangeJson, BlazorMonacoJsonContext.Default.Range);
        try
        {
            var text = await document.GetTextAsync(cancellationToken);
            var lines = text.Lines;
            var span = range is null ? text.FullRange : range.ToSpan(lines);

            var result = (await document.GetCodeActionsAsync(span, cancellationToken)).ToImmutableArray();
            var time1 = sw.ElapsedMilliseconds;
            sw.Restart();

            var converted = await ToCodeActionsAsync(result, document, cancellationToken);

            var json = JsonSerializer.Serialize(converted, BlazorMonacoJsonContext.Default.ImmutableArrayMonacoCodeAction);

            var time2 = sw.ElapsedMilliseconds;
            logger.LogDebug("Got code actions ({Count}) for {Range} in {Time1} + {Time2} ms", converted.Length, range.Stringify(), time1.SeparateThousands(), time2.SeparateThousands());

            return json;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Canceled code actions for {Range} in {Time} ms", range.Stringify(), sw.ElapsedMilliseconds.SeparateThousands());

            // `null` will be transformed into an exception at the front end.
            return null;
        }
    }

    private async Task<ImmutableArray<MonacoCodeAction>> ToCodeActionsAsync(ImmutableArray<RoslynCodeAction> codeActions, Document sourceDocument, CancellationToken cancellationToken)
    {
        var solution = sourceDocument.Project.Solution;
        var textDiffService = solution.Services.GetDocumentTextDifferencingService();

        var result = ImmutableArray.CreateBuilder<MonacoCodeAction>();

        foreach (var (prefix, codeAction) in flatten(null, codeActions))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var operations = await codeAction.GetOperationsAsync(solution, NullProgress<CodeAnalysisProgress>.Instance, cancellationToken);

            var edits = ImmutableArray.CreateBuilder<WorkspaceTextEdit>();

            foreach (var operation in operations)
            {
                if (operation is not ApplyChangesOperation applyChangesOperation)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var changes = applyChangesOperation.ChangedSolution.GetChanges(solution);
                var newSolution = await applyChangesOperation.ChangedSolution.WithMergedLinkedFileChangesAsync(solution, changes, cancellationToken);
                changes = newSolution.GetChanges(solution);

                var projectChanges = changes.GetProjectChanges();

                foreach (var documentId in projectChanges.SelectMany(pc => pc.GetChangedDocuments()))
                {
                    var newDocument = newSolution.GetDocument(documentId);
                    var oldDocument = solution.GetDocument(documentId);

                    if (oldDocument is null || newDocument is null ||
                        !modelUris.TryGetValue(newDocument.Id, out var newUri))
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    var textChanges = await textDiffService.GetTextChangesAsync(oldDocument, newDocument, cancellationToken);

                    var oldText = await oldDocument.GetTextAsync(cancellationToken);

                    foreach (var textChange in textChanges)
                    {
                        edits.Add(new WorkspaceTextEdit
                        {
                            ResourceUri = newUri,
                            TextEdit = new()
                            {
                                Text = textChange.NewText ?? string.Empty,
                                Range = textChange.Span.ToRange(oldText.Lines),
                            },
                        });
                    }
                }
            }

            if (edits.Count != 0)
            {
                result.Add(new()
                {
                    Title = addPrefix(prefix, codeAction.Title),
                    Kind = MonacoCodeActionKind.QuickFix,
                    Edit = new()
                    {
                        Edits = edits.DrainToImmutable(),
                    },
                });
            }
        }

        return result.DrainToImmutable();

        static IEnumerable<(string? Prefix, RoslynCodeAction Action)> flatten(string? prefix, ImmutableArray<RoslynCodeAction> codeActions)
        {
            foreach (var codeAction in codeActions)
            {
                if (codeAction.NestedActions.IsDefaultOrEmpty)
                {
                    yield return (prefix, codeAction);
                    continue;
                }

                var nestedPrefix = addPrefix(prefix, codeAction.Title);
                foreach (var nestedCodeAction in flatten(nestedPrefix, codeAction.NestedActions))
                {
                    yield return nestedCodeAction;
                }
            }
        }

        static string addPrefix(string? prefix, string nested)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                return nested;
            }

            return $"{prefix}: {nested}";
        }
    }

    /// <returns>
    /// Markdown or <see langword="null"/> if cancelled.
    /// </returns>
    /// <remarks>
    /// For inspiration, see <see href="https://github.com/dotnet/roslyn/blob/ad14335550de1134f0b5a59b6cd040001d0d8c8d/src/LanguageServer/Protocol/Handler/Hover/HoverHandler.cs#L26"/>.
    /// </remarks>
    public async Task<string?> ProvideHoverAsync(string modelUri, string positionJson, CancellationToken cancellationToken)
    {
        if (!TryGetDocument(modelUri, out var document))
        {
            return "";
        }

        var sw = Stopwatch.StartNew();
        var position = JsonSerializer.Deserialize(positionJson, BlazorMonacoJsonContext.Default.Position)!;
        try
        {
            var text = await document.GetTextAsync(cancellationToken);
            int caretPosition = text.Lines.GetPosition(position.ToLinePosition());
            var quickInfoService = document.Project.Services.GetRequiredService<QuickInfoService>();
            var quickInfo = await quickInfoService.GetQuickInfoAsync(document, caretPosition, cancellationToken);
            if (quickInfo == null)
            {
                return "";
            }

            // Insert line breaks in between sections to ensure we get double spacing between sections.
            var tags = quickInfo.Sections.SelectMany(static s =>
                s.TaggedParts.Add(new TaggedText(TextTags.LineBreak, Environment.NewLine)));

            var markdown = MonacoConversions.GetMarkdown(tags, document.Project.Language);

            logger.LogDebug("Got hover ({Length}) for {Position} in {Time} ms", markdown.Length, position.Stringify(), sw.ElapsedMilliseconds.SeparateThousands());

            return markdown;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Canceled hover for {Position} in {Time} ms", position.Stringify(), sw.ElapsedMilliseconds.SeparateThousands());

            return null;
        }
    }

    /// <returns>
    /// JSON-serialized <see cref="SignatureHelp"/>.
    /// We serialize here to avoid serializing twice unnecessarily
    /// (first on Worker to App interface, then on App to Monaco interface).
    /// </returns>
    /// <remarks>
    /// For inspiration, see <see href="https://github.com/dotnet/roslyn/blob/ad14335550de1134f0b5a59b6cd040001d0d8c8d/src/LanguageServer/Protocol/Handler/SignatureHelp/SignatureHelpHandler.cs#L25"/>.
    /// </remarks>
    public async Task<string?> ProvideSignatureHelpAsync(string modelUri, string positionJson, string contextJson, CancellationToken cancellationToken)
    {
        if (!TryGetDocument(modelUri, out var document))
        {
            return "null";
        }

        var sw = Stopwatch.StartNew();
        var position = JsonSerializer.Deserialize(positionJson, BlazorMonacoJsonContext.Default.Position)!;
        try
        {
            var text = await document.GetTextAsync(cancellationToken);
            int caretPosition = text.Lines.GetPosition(position.ToLinePosition());
            var context = JsonSerializer.Deserialize(contextJson, BlazorMonacoJsonContext.Default.SignatureHelpContext)!;
            var signatureHelp = await document.GetSignatureHelpAsync(caretPosition, context.ToReason(), context.TriggerCharacter, cancellationToken);
            var signatureHelpJson = JsonSerializer.Serialize(signatureHelp, BlazorMonacoJsonContext.Default.SignatureHelp);

            logger.LogDebug("Got signature help ({Length}) for {Position} in {Time} ms", signatureHelpJson.Length, position.Stringify(), sw.ElapsedMilliseconds.SeparateThousands());

            return signatureHelpJson;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Canceled signature help for {Position} in {Time} ms", position.Stringify(), sw.ElapsedMilliseconds.SeparateThousands());

            return null;
        }
    }

    public void OnCachedCompilationLoaded(CompiledAssembly output)
    {
        compilerDiagnostics = output;
    }

    public async void OnCompilationFinished()
    {
        compilerDiagnostics = compiler.LastResult?.Output.CompiledAssembly;

        try
        {
            using var _ = await workspaceLock.LockAsync();

            // Modify the configuration project.
            {
                var project = GetProject(configuration: true);

                if (!additionalConfigurationReferences.IsDefault)
                {
                    foreach (var reference in additionalConfigurationReferences)
                    {
                        project = project.RemoveMetadataReference(reference);
                    }
                }

                if (compiler.LastResult?.Output.CompilerAssemblies is not { } compilerAssemblies)
                {
                    additionalConfigurationReferences = default;
                }
                else
                {
                    additionalConfigurationReferences = compilerAssemblies
                        .SelectAsArray(MetadataReference (p) => MetadataReference.LoadFromBytesOrDisk(p.Key, p.Value));
                    project = project.AddMetadataReferences(additionalConfigurationReferences);
                }

                ApplyChanges(project.Solution);
            }

            // Modify the main project.
            {
                var project = GetProject(configuration: false);

                if (compiler.LastResult?.Output.CSharpParseOptions is { } parseOptions)
                {
                    project = project.WithParseOptions(parseOptions);
                }
                else
                {
                    project = project.WithParseOptions(Compiler.CreateDefaultParseOptions());
                }

                if (compiler.LastResult?.Output.CSharpCompilationOptions is { } options)
                {
                    project = project.WithCompilationOptions(options);
                }
                else
                {
                    project = project.WithCompilationOptions(Compiler.CreateDefaultCompilationOptions(defaultOutputKind));
                }

                if (!additionalSourceDocuments.IsDefaultOrEmpty)
                {
                    project = project.RemoveDocuments(additionalSourceDocuments);
                }

                if (compiler.LastResult?.Output.AdditionalSources is { IsDefaultOrEmpty: false } additionalSources)
                {
                    var additionalSourceDocumentBuilder = ImmutableArray.CreateBuilder<DocumentId>(additionalSources.Length);
                    foreach (var tree in additionalSources)
                    {
                        var document = project.AddDocument(
                            name: tree.FilePath,
                            text: tree.GetText(),
                            filePath: tree.FilePath);
                        additionalSourceDocumentBuilder.Add(document.Id);
                        project = document.Project;
                    }
                    additionalSourceDocuments = additionalSourceDocumentBuilder.DrainToImmutable();
                }
                else
                {
                    additionalSourceDocuments = [];
                }

                if (compiler.LastResult?.Output.ReferenceAssemblies is { } references)
                {
                    project = project.WithMetadataReferences(references);
                }
                else
                {
                    project = project.WithMetadataReferences(RefAssemblyMetadata.All);
                }

                ApplyChanges(project.Solution);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle finished compilation.");
        }
    }

    private void InvalidateCompilerCache()
    {
        compilerDiagnostics = null;
    }

    public async Task OnDidChangeWorkspaceAsync(ImmutableArray<ModelInfo> models, bool refresh)
    {
        if (!refresh) InvalidateCompilerCache();

        using var _ = await workspaceLock.LockAsync();

        var modelLookupByUri = models.ToDictionary(m => m.Uri);

        // Make sure our workspace matches `models`.
        foreach (DocumentId docId in GetDocumentIds())
        {
            if (modelUris.TryGetValue(docId, out string? modelUri))
            {
                // We have URI of this document, it's in our workspace.

                var doc = GetDocument(docId)!;
                if (modelLookupByUri.TryGetValue(modelUri, out ModelInfo? model))
                {
                    // The document is still present in `models`.

                    if (doc.Name != model.FileName)
                    {
                        // Document has been renamed.
                        modelUris.Remove(docId);

                        if (model.FileName.IsCSharpFileName())
                        {
                            modelUris.Add(docId, model.Uri);
                            ApplyChanges(workspace.CurrentSolution.WithDocumentFilePath(docId, model.FileName));

                            if (model.NewContent != null)
                            {
                                // The caller sets the content to reset the state.
                                doc = GetDocument(docId)!;
                                ApplyChanges(doc.WithText(SourceText.From(model.NewContent)).Project.Solution);
                            }
                        }
                        else
                        {
                            ApplyChanges(doc.Project.RemoveDocument(docId).Solution);
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
                    modelUris.Remove(docId);
                    ApplyChanges(doc.Project.RemoveDocument(docId).Solution);
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
                var doc = GetProject(configuration: model.IsConfiguration).AddDocument(
                    name: model.FileName,
                    text: model.NewContent ?? string.Empty,
                    filePath: model.FileName);
                modelUris.Add(doc.Id, model.Uri);
                ApplyChanges(doc.Project.Solution);
            }
        }

        await UpdateOptionsIfNecessaryAsync();
    }

    private async Task UpdateOptionsIfNecessaryAsync()
    {
        var project = GetProject(configuration: false);
        var sources = await project.Documents.SelectNonNullAsync(d => d.GetSyntaxTreeAsync());
        defaultOutputKind = Compiler.GetDefaultOutputKind(sources);
        if (project.CompilationOptions is { } options &&
            options.OutputKind != defaultOutputKind &&
            compiler.LastResult?.Output.CSharpCompilationOptions is null)
        {
            ApplyChanges(project.WithCompilationOptions(options.WithOutputKind(defaultOutputKind)).Solution);
        }
    }

    public async Task OnDidChangeModelContentAsync(string modelUri, ModelContentChangedEvent args)
    {
        if (!TryGetDocument(modelUri, out var document))
        {
            logger.LogWarning("Document to change content of not found: {ModelUri}", modelUri);
            return;
        }

        InvalidateCompilerCache();

        using var _ = await workspaceLock.LockAsync();

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

    private static readonly DiagnosticDataComparer s_diagnosticDataComparer = new(
        // The file path is not preserved (since we only gather diagnostics for a single file) and can differ slightly (e.g., leading slash).
        excludeFilePath: true,
        // IDE markers have downgraded info severity, so we exclude severity when comparing them with compiler markers.
        excludeSeverity: true);

    public async Task<ImmutableArray<MarkerData>> GetDiagnosticsAsync(string modelUri)
    {
        var ideDiagnostics = TryGetDocument(modelUri, out var document)
            ? await document.GetDiagnosticsAsync()
            : [];

        DiagnosticData[] compilerDiagnostics;
        if (this.compilerDiagnostics is { } compiled &&
            compiled.Diagnostics.Length > 0 &&
            CompiledAssembly.TryParseInputModelUri(modelUri, out var fileName))
        {
            compilerDiagnostics = compiled.Diagnostics
                .Where(d => d.FilePath == compiled.BaseDirectory + fileName)
                .ToArray();
        }
        else
        {
            compilerDiagnostics = [];
        }

        var filteredIdeDiagnostics = ideDiagnostics
            .Except(compilerDiagnostics, s_diagnosticDataComparer);

        return compilerDiagnostics.Select(static d => d.ToMarkerData())
            .Concat(filteredIdeDiagnostics.Select(static d => d.ToMarkerData(downgradeInfo: true)))
            .ToImmutableArray();
    }

    private void ApplyChanges(Solution solution, [CallerMemberName] string memberName = "", [CallerLineNumber] int lineNumber = -1)
    {
        if (!workspace.TryApplyChanges(solution))
        {
            logger.LogWarning("Failed to apply changes to the workspace from '{Member}:{Line}'.", memberName, lineNumber);
        }
        else
        {
            logger.LogTrace("Successfully applied changes to the workspace from '{Member}:{Line}'.", memberName, lineNumber);
        }
    }

    private bool TryGetDocument(string modelUri, [NotNullWhen(returnValue: true)] out Document? document)
    {
        var id = GetDocumentIds().FirstOrDefault(id =>
            modelUris.TryGetValue(id, out var uri) &&
            uri == modelUri);
        if (id != null)
        {
            document = GetDocument(id);
            return document != null;
        }

        document = null;
        return false;
    }

    private Document? GetDocument(DocumentId id)
    {
        return workspace.CurrentSolution.Projects
            .SelectNonNull(p => p.GetDocument(id))
            .FirstOrDefault();
    }

    private IEnumerable<DocumentId> GetDocumentIds()
    {
        return workspace.CurrentSolution.Projects.SelectMany(p => p.DocumentIds);
    }

    private Project GetProject(bool configuration)
    {
        return workspace.CurrentSolution.GetProject(configuration ? configurationProjectId : projectId)!;
    }
}
