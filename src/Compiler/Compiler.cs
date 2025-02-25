using Microsoft.AspNetCore.Mvc.Razor.Extensions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.Runtime.Loader;

namespace DotNetLab;

public class Compiler(ILogger<Compiler> logger) : ICompiler
{
    private (CompilationInput Input, CompiledAssembly Output)? lastResult;

    public CompiledAssembly Compile(
        CompilationInput input,
        ImmutableDictionary<string, ImmutableArray<byte>>? assemblies,
        ImmutableDictionary<string, ImmutableArray<byte>>? builtInAssemblies,
        AssemblyLoadContext alc)
    {
        if (lastResult is { } cached)
        {
            if (input.Equals(cached.Input))
            {
                return cached.Output;
            }
        }

        var result = CompileNoCache(input, assemblies, builtInAssemblies, alc);
        lastResult = (input, result);
        return result;
    }

    private CompiledAssembly CompileNoCache(
        CompilationInput compilationInput,
        ImmutableDictionary<string, ImmutableArray<byte>>? assemblies,
        ImmutableDictionary<string, ImmutableArray<byte>>? builtInAssemblies,
        AssemblyLoadContext alc)
    {
        // IMPORTANT: Keep in sync with `InitialCode.Configuration`.
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview)
            .WithFeatures([new("use-roslyn-tokenizer", "true")]);

        var references = Basic.Reference.Assemblies.AspNet90.References.All;

        // If we have a configuration, compile and execute it.
        if (compilationInput.Configuration is { } configuration)
        {
            var configCompilation = CSharpCompilation.Create(
                assemblyName: "Configuration",
                syntaxTrees:
                [
                    CSharpSyntaxTree.ParseText(configuration, parseOptions, "Configuration.cs", Encoding.UTF8),
                    CSharpSyntaxTree.ParseText("""
                        global using DotNetLab;
                        global using Microsoft.CodeAnalysis.CSharp;
                        global using System;
                        """, parseOptions, "GlobalUsings.cs", Encoding.UTF8)
                ],
                references:
                [
                    ..references,
                    ..assemblies!.Values.Select(b => MetadataReference.CreateFromImage(b)),
                ],
                options: createCompilationOptions(OutputKind.ConsoleApplication));

            var emitStream = getEmitStream(configCompilation)
                // If compilation fails, it might be because older Roslyn is referenced, re-try with built-in versions.
                ?? getEmitStream(configCompilation.WithReferences(
                    [
                        ..references,
                        ..builtInAssemblies!.Values.Select(b => MetadataReference.CreateFromImage(b)),
                    ]))
                ?? throw new InvalidOperationException("Cannot execute configuration due to compilation errors:" +
                    Environment.NewLine +
                    getEmitDiagnostics(configCompilation).JoinToString(Environment.NewLine));

            var configAssembly = alc.LoadFromStream(emitStream);

            var entryPoint = configAssembly.EntryPoint
                ?? throw new ArgumentException("No entry point found in the configuration assembly.");

            Config.Reset();

            Executor.InvokeEntryPoint(entryPoint);

            parseOptions = Config.CurrentCSharpParseOptions;

            logger.LogDebug("Using language version {LangVersion} (specified {SpecifiedLangVersion})", parseOptions.LanguageVersion, parseOptions.SpecifiedLanguageVersion);
        }

        var directory = "/TestProject/";
        var fileSystem = new VirtualRazorProjectFileSystemProxy();
        var cSharp = new Dictionary<string, CSharpSyntaxTree>();
        foreach (var input in compilationInput.Inputs.Value)
        {
            var filePath = directory + input.FileName;
            switch (input.FileExtension)
            {
                case ".razor":
                case ".cshtml":
                    {
                        var item = RazorAccessors.CreateSourceGeneratorProjectItem(
                            basePath: "/",
                            filePath: filePath,
                            relativePhysicalPath: input.FileName,
                            fileKind: null!, // will be automatically determined from file path
                            additionalText: new TestAdditionalText(input.Text, encoding: Encoding.UTF8, path: filePath),
                            cssScope: null);
                        fileSystem.Add(item);
                        break;
                    }
                case ".cs":
                    {
                        cSharp[input.FileName] = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(input.Text, parseOptions, path: filePath, Encoding.UTF8);
                        break;
                    }
            }
        }

        // Choose output kind EXE if there are top-level statements, otherwise DLL.
        var outputKind = cSharp.Values.Any(tree => tree.GetRoot().DescendantNodes().OfType<GlobalStatementSyntax>().Any())
            ? OutputKind.ConsoleApplication
            : OutputKind.DynamicallyLinkedLibrary;

        var options = createCompilationOptions(outputKind);

        var config = RazorConfiguration.Default;

        // Phase 1: Declaration only (to be used as a reference from which tag helpers will be discovered).
        RazorProjectEngine declarationProjectEngine = createProjectEngine([]);
        var declarationCompilation = CSharpCompilation.Create("TestAssembly",
            syntaxTrees: [
                ..fileSystem.Inner.EnumerateItemsSafe("/").Select((item) =>
                {
                    RazorCodeDocument declarationCodeDocument = declarationProjectEngine.ProcessDeclarationOnlySafe(item);
                    string declarationCSharp = declarationCodeDocument.GetCSharpDocument().GetGeneratedCode();
                    return CSharpSyntaxTree.ParseText(declarationCSharp, parseOptions, encoding: Encoding.UTF8);
                }),
                ..cSharp.Values,
            ],
            references,
            options);

        // Phase 2: Full generation.
        RazorProjectEngine projectEngine = createProjectEngine([
            ..references,
            declarationCompilation.ToMetadataReference()]);
        List<Diagnostic> allRazorDiagnostics = new();
        var compiledRazorFiles = fileSystem.Inner.EnumerateItemsSafe("/")
            .ToImmutableSortedDictionary(
                keySelector: (item) => item.RelativePhysicalPath,
                elementSelector: (item) =>
                {
                    RazorCodeDocument codeDocument = projectEngine.ProcessSafe(item);
                    RazorCodeDocument designTimeDocument = projectEngine.ProcessDesignTimeSafe(item);

                    string syntax = codeDocument.GetSyntaxTree().Serialize();
                    string ir = codeDocument.GetDocumentIntermediateNode().Serialize();
                    string cSharp = codeDocument.GetCSharpDocument().GetGeneratedCode();
                    IReadOnlyList<RazorDiagnostic> razorDiagnosticsOriginal = codeDocument.GetCSharpDocument().GetDiagnostics();
                    string razorDiagnostics = razorDiagnosticsOriginal.JoinToString(Environment.NewLine);

                    string designSyntax = designTimeDocument.GetSyntaxTree().Serialize();
                    string designIr = designTimeDocument.GetDocumentIntermediateNode().Serialize();
                    string designCSharp = designTimeDocument.GetCSharpDocument().GetGeneratedCode();
                    string designRazorDiagnostics = designTimeDocument.GetCSharpDocument().GetDiagnostics().JoinToString(Environment.NewLine);

                    allRazorDiagnostics.AddRange(razorDiagnosticsOriginal.Select(RazorUtil.ToDiagnostic));

                    return new CompiledFile([
                        new() { Type = "syntax", Label = "Syntax", EagerText = syntax, DesignTimeText = designSyntax },
                        new() { Type = "ir", Label = "IR", Language = "csharp", EagerText = ir, DesignTimeText = designIr },
                        ..(string.IsNullOrEmpty(razorDiagnostics) && string.IsNullOrEmpty(designRazorDiagnostics)
                            ? ImmutableArray<CompiledFileOutput>.Empty
                            : [new() { Type = "razorErrors", Label = "Razor Error List", EagerText = razorDiagnostics, DesignTimeText = designRazorDiagnostics }]),
                        new() { Type = "cs", Label = "C#", Language = "csharp", EagerText = cSharp, DesignTimeText = designCSharp, Priority = 1 },
                    ]);
                });

        var finalCompilation = CSharpCompilation.Create("TestAssembly",
            [
                ..compiledRazorFiles.Values.Select((file) =>
                {
                    var cSharpText = file.GetOutput("cs")!.EagerText!;
                    return CSharpSyntaxTree.ParseText(cSharpText, parseOptions, encoding: Encoding.UTF8);
                }),
                ..cSharp.Values,
            ],
            references,
            options);

        ICSharpCode.Decompiler.Metadata.PEFile? peFile = null;

        var compiledFiles = compiledRazorFiles.AddRange(
            cSharp.Select((pair) => new KeyValuePair<string, CompiledFile>(
                pair.Key,
                new([
                    new() { Type = "syntax", Label = "Syntax", EagerText = pair.Value.GetRoot().Dump() },
                    new() { Type = "syntaxTrivia", Label = "Syntax + Trivia", EagerText = pair.Value.GetRoot().DumpExtended() },
                    new()
                    {
                        Type = "il",
                        Label = "IL",
                        Language = "csharp",
                        LazyText = () =>
                        {
                            peFile ??= getPeFile(finalCompilation);
                            return new(getIl(peFile));
                        },
                    },
                    new()
                    {
                        Type = "seq",
                        Label = "Sequence points",
                        LazyText = async () =>
                        {
                            peFile ??= getPeFile(finalCompilation);
                            return await getSequencePoints(peFile);
                        },
                    },
                    new()
                    {
                        Type = "cs",
                        Label = "C#",
                        Language = "csharp",
                        LazyText = async () =>
                        {
                            peFile ??= getPeFile(finalCompilation);
                            return await getCSharpAsync(peFile);
                        },
                    },
                    new()
                    {
                        Type = "run",
                        Label = "Run",
                        LazyText = () =>
                        {
                            var executableCompilation = finalCompilation.Options.OutputKind == OutputKind.ConsoleApplication
                                ? finalCompilation
                                : finalCompilation.WithOptions(finalCompilation.Options.WithOutputKind(OutputKind.ConsoleApplication));
                            var emitStream = getEmitStream(executableCompilation);
                            string output = emitStream is null
                                ? executableCompilation.GetDiagnostics().FirstOrDefault(d => d.Id == "CS5001") is { } error
                                    ? error.GetMessage(CultureInfo.InvariantCulture)
                                    : "Cannot execute due to compilation errors."
                                : Executor.Execute(emitStream);
                            return new(output);
                        },
                        Priority = 1,
                    },
                ]))));

        IEnumerable<Diagnostic> diagnostics = getEmitDiagnostics(finalCompilation)
            .Concat(allRazorDiagnostics)
            .Where(d => d.Severity != DiagnosticSeverity.Hidden);
        string diagnosticsText = diagnostics.GetDiagnosticsText();
        int numWarnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        int numErrors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        ImmutableArray<DiagnosticData> diagnosticData = diagnostics
            .Select(d => d.ToDiagnosticData())
            .ToImmutableArray();

        var result = new CompiledAssembly(
            BaseDirectory: directory,
            Files: compiledFiles,
            NumWarnings: numWarnings,
            NumErrors: numErrors,
            Diagnostics: diagnosticData,
            GlobalOutputs:
            [
                new()
                {
                    Type = CompiledAssembly.DiagnosticsOutputType,
                    Label = CompiledAssembly.DiagnosticsOutputLabel,
                    Language = "csharp",
                    EagerText = diagnosticsText,
                    Priority = numErrors > 0 ? 2 : 0,
                },
            ]);

        return result;

        static CSharpCompilationOptions createCompilationOptions(OutputKind outputKind)
        {
            return new CSharpCompilationOptions(
                outputKind,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable,
                concurrentBuild: false);
        }

        RazorProjectEngine createProjectEngine(IReadOnlyList<MetadataReference> references)
        {
            return RazorProjectEngine.Create(config, fileSystem.Inner, b =>
            {
                b.SetRootNamespace("TestNamespace");

                b.Features.Add(RazorAccessors.CreateDefaultTypeNameFeature());
                b.Features.Add(new CompilationTagHelperFeature());
                b.Features.Add(new DefaultMetadataReferenceFeature
                {
                    References = references,
                });

                b.Features.Add(new ConfigureRazorParserOptions(parseOptions));

                CompilerFeatures.Register(b);
                RazorExtensions.Register(b);

                b.SetCSharpLanguageVersion(LanguageVersion.Preview);
            });
        }

        MemoryStream? getEmitStream(CSharpCompilation compilation)
        {
            var stream = new MemoryStream();
            var emitResult = compilation.Emit(stream);
            if (!emitResult.Success)
            {
                logger.LogDebug("Emit failed: {Diagnostics}", emitResult.Diagnostics);
                return null;
            }

            stream.Position = 0;
            return stream;
        }

        static ImmutableArray<Diagnostic> getEmitDiagnostics(CSharpCompilation compilation)
        {
            var emitResult = compilation.Emit(new MemoryStream());
            return emitResult.Diagnostics;
        }

        ICSharpCode.Decompiler.Metadata.PEFile? getPeFile(CSharpCompilation compilation)
        {
            return getEmitStream(compilation) is { } stream
                ? new(compilation.AssemblyName ?? "", stream)
                : null;
        }

        static string getIl(ICSharpCode.Decompiler.Metadata.PEFile? peFile)
        {
            if (peFile is null)
            {
                return "";
            }

            var output = new ICSharpCode.Decompiler.PlainTextOutput();
            var disassembler = new ICSharpCode.Decompiler.Disassembler.ReflectionDisassembler(output, cancellationToken: default);
            disassembler.WriteModuleContents(peFile);
            return output.ToString();
        }

        // Inspired by https://github.com/icsharpcode/ILSpy/pull/1040.
        static async Task<string> getSequencePoints(ICSharpCode.Decompiler.Metadata.PEFile? peFile)
        {
            if (peFile is null)
            {
                return "";
            }

            var typeSystem = await getCSharpDecompilerTypeSystemAsync(peFile);
            var settings = getCSharpDecompilerSettings();
            var decompiler = new ICSharpCode.Decompiler.CSharp.CSharpDecompiler(typeSystem, settings);

            var output = new StringWriter();
            ICSharpCode.Decompiler.CSharp.OutputVisitor.TokenWriter tokenWriter = new ICSharpCode.Decompiler.CSharp.OutputVisitor.TextWriterTokenWriter(output);
            tokenWriter = ICSharpCode.Decompiler.CSharp.OutputVisitor.TokenWriter.WrapInWriterThatSetsLocationsInAST(tokenWriter);

            var syntaxTree = decompiler.DecompileWholeModuleAsSingleFile();
            syntaxTree.AcceptVisitor(new ICSharpCode.Decompiler.CSharp.OutputVisitor.InsertParenthesesVisitor { InsertParenthesesForReadability = true });
            syntaxTree.AcceptVisitor(new ICSharpCode.Decompiler.CSharp.OutputVisitor.CSharpOutputVisitor(tokenWriter, settings.CSharpFormattingOptions));

            using var sequencePoints = decompiler.CreateSequencePoints(syntaxTree)
                .SelectMany(p => p.Value.Select(s => (Function: p.Key, SequencePoint: s)))
                .GetEnumerator();

            var lineIndex = -1;
            var lines = output.ToString().AsSpan().EnumerateLines().GetEnumerator();

            var result = new StringBuilder();

            while (true)
            {
                if (!sequencePoints.MoveNext())
                {
                    break;
                }

                var (function, sp) = sequencePoints.Current;

                if (sp.IsHidden)
                {
                    continue;
                }

                // Find the corresponding line.
                var targetLineIndex = sp.StartLine - 1;
                while (lineIndex < targetLineIndex && lines.MoveNext())
                {
                    lineIndex++;
                }

                if (lineIndex < 0 || lineIndex != targetLineIndex)
                {
                    break;
                }

                var line = lines.Current;
                var text = line[(sp.StartColumn - 1)..(sp.EndColumn - 1)];
                result.AppendLine($"{function.Name}(IL_{sp.Offset:x4}-IL_{sp.EndOffset:x4} {sp.StartLine}:{sp.StartColumn}-{sp.EndLine}:{sp.EndColumn}): {text}");
            }

            return result.ToString();
        }

        static async Task<string> getCSharpAsync(ICSharpCode.Decompiler.Metadata.PEFile? peFile)
        {
            if (peFile is null)
            {
                return "";
            }

            var decompiler = await getCSharpDecompilerAsync(peFile);
            return decompiler.DecompileWholeModuleAsString();
        }

        static async Task<ICSharpCode.Decompiler.CSharp.CSharpDecompiler> getCSharpDecompilerAsync(ICSharpCode.Decompiler.Metadata.PEFile peFile)
        {
            return new ICSharpCode.Decompiler.CSharp.CSharpDecompiler(
                await getCSharpDecompilerTypeSystemAsync(peFile),
                getCSharpDecompilerSettings());
        }

        static async Task<ICSharpCode.Decompiler.TypeSystem.DecompilerTypeSystem> getCSharpDecompilerTypeSystemAsync(ICSharpCode.Decompiler.Metadata.PEFile peFile)
        {
            return await ICSharpCode.Decompiler.TypeSystem.DecompilerTypeSystem.CreateAsync(
                peFile,
                new ICSharpCode.Decompiler.Metadata.UniversalAssemblyResolver(
                    mainAssemblyFileName: null,
                    throwOnError: false,
                    targetFramework: ".NETCoreApp,Version=9.0"));
        }

        static ICSharpCode.Decompiler.DecompilerSettings getCSharpDecompilerSettings()
        {
            return new ICSharpCode.Decompiler.DecompilerSettings(ICSharpCode.Decompiler.CSharp.LanguageVersion.CSharp1);
        }
    }
}

internal sealed class TestAdditionalText(string path, SourceText text) : AdditionalText
{
    public TestAdditionalText(string text = "", Encoding? encoding = null, string path = "dummy")
        : this(path, SourceText.From(text, encoding))
    {
    }

    public override string Path => path;

    public override SourceText GetText(CancellationToken cancellationToken = default) => text;
}

internal sealed class ConfigureRazorParserOptions(CSharpParseOptions cSharpParseOptions)
    : RazorEngineFeatureBase, IConfigureRazorParserOptionsFeature
{
    public int Order { get; set; }

    public void Configure(RazorParserOptionsBuilder options)
    {
        if (options.GetType().GetProperty("UseRoslynTokenizer") is { } useRoslynTokenizerProperty)
        {
            var useRoslynTokenizer = cSharpParseOptions.Features.TryGetValue("use-roslyn-tokenizer", out var useRoslynTokenizerValue) &&
                string.Equals(useRoslynTokenizerValue, bool.TrueString, StringComparison.OrdinalIgnoreCase);
            useRoslynTokenizerProperty.SetValue(options, useRoslynTokenizer);
        }

        if (options.GetType().GetProperty("CSharpParseOptions") is { } cSharpParseOptionsProperty)
        {
            cSharpParseOptionsProperty.SetValue(options, cSharpParseOptions);
        }
    }
}
