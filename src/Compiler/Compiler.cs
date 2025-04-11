using Microsoft.AspNetCore.Mvc.Razor.Extensions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.NET.Sdk.Razor.SourceGenerators;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using ReferenceInfo = Basic.Reference.Assemblies.AspNet90.ReferenceInfo;

namespace DotNetLab;

public class Compiler(
    ILogger<Compiler> logger,
    ILogger<DecompilerAssemblyResolver> decompilerAssemblyResolverLogger) : ICompiler
{
    private const string ToolchainHelpText = """

        You can try selecting different Razor toolchain in Settings / Advanced.
        """;

    private (CompilationInput Input, CompiledAssembly Output)? lastResult;

    /// <summary>
    /// Reused for incremental source generation.
    /// </summary>
    private GeneratorDriver? generatorDriver;

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
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable,
            concurrentBuild: false);

        var references = Basic.Reference.Assemblies.AspNet90.References.All;
        var referenceInfos = Basic.Reference.Assemblies.AspNet90.ReferenceInfos.All;

        // If we have a configuration, compile and execute it.
        if (compilationInput.Configuration is { } configuration)
        {
            executeConfiguration(configuration);
        }

        const string projectName = "TestProject";
        const string directory = "/";

        var optionsProvider = new TestAnalyzerConfigOptionsProvider
        {
            GlobalOptions =
            {
                ["build_property.RazorConfiguration"] = "Default",
                ["build_property.RootNamespace"] = "TestNamespace",
                ["build_property.RazorLangVersion"] = "Latest",
                ["build_property.GenerateRazorMetadataSourceChecksumAttributes"] = "false",
            },
        };

        var cSharpSources = new List<(InputCode Input, CSharpSyntaxTree SyntaxTree)>();
        var msBuildSources = new List<InputCode>();
        var additionalSources = new List<InputCode>();

        CSharpParseOptions? scriptOptions = null;

        foreach (var input in compilationInput.Inputs.Value)
        {
            if (isCSharp(input, out bool script))
            {
                if (script)
                {
                    scriptOptions ??= parseOptions.WithKind(SourceCodeKind.Script);
                }

                var filePath = getFilePath(input);
                var currentParseOptions = script ? scriptOptions : parseOptions;
                var syntaxTree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(input.Text, currentParseOptions, filePath, Encoding.UTF8);
                cSharpSources.Add((input, syntaxTree));
            }
            else if (".proj".Equals(input.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                msBuildSources.Add(input);
            }
            else
            {
                additionalSources.Add(input);
            }
        }

        // Choose output kind EXE if there are top-level statements, otherwise DLL.
        var outputKind = cSharpSources.Any(static s => s.SyntaxTree.GetRoot().ChildNodes().OfType<GlobalStatementSyntax>().Any())
            ? OutputKind.ConsoleApplication
            : OutputKind.DynamicallyLinkedLibrary;

        options = options.WithOutputKind(outputKind);

        GeneratorRunResult razorResult = default;
        ImmutableDictionary<string, (RazorCodeDocument Runtime, RazorCodeDocument DesignTime)>? razorMap = null;

        var effectiveToolchain = compilationInput.RazorToolchain switch
        {
            RazorToolchain.SourceGeneratorOrInternalApi =>
                compilationInput.RazorStrategy == RazorStrategy.DesignTime
                    ? RazorToolchain.InternalApi
                    : RazorToolchain.SourceGenerator,
            var other => other,
        };

        var (finalCompilation, additionalDiagnostics) = effectiveToolchain switch
        {
            RazorToolchain.SourceGenerator => runRazorSourceGenerator(),
            RazorToolchain.InternalApi => runRazorInternalApi(),
            var other => throw new InvalidOperationException($"Invalid Razor toolchain '{other}'."),
        };

        ICSharpCode.Decompiler.Metadata.PEFile? peFile = null;

        IEnumerable<Diagnostic> diagnostics = getEmitDiagnostics(finalCompilation)
            .Concat(additionalDiagnostics)
            .Where(d => d.Severity != DiagnosticSeverity.Hidden);
        string diagnosticsText = diagnostics.GetDiagnosticsText();
        int numWarnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        int numErrors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        ImmutableArray<DiagnosticData> diagnosticData = diagnostics
            .Select(d => d.ToDiagnosticData())
            .ToImmutableArray();

        var result = new CompiledAssembly(
            BaseDirectory: directory,
            Files: cSharpSources.Select(static (cSharpSource) =>
            {
                var syntaxTree = cSharpSource.SyntaxTree;
                var compiledFile = new CompiledFile([
                    new() { Type = "syntax", Label = "Syntax", EagerText = syntaxTree.GetRoot().Dump() },
                    new() { Type = "syntaxTrivia", Label = "Trivia", EagerText = syntaxTree.GetRoot().DumpExtended() },
                ]);
                return KeyValuePair.Create(cSharpSource.Input.FileName, compiledFile);
            })
            .Concat(msBuildSources.Select((input) =>
            {
                var compiledFile = new CompiledFile(
                [
                    new()
                    {
                        Type = "eval",
                        Label = "Evaluated",
                        Language = "xml",
                        EagerText = MsBuildEvaluator.Evaluate(input.Text),
                        Priority = 1,
                    },
                ]);

                return KeyValuePair.Create(input.FileName, compiledFile);
            }))
            .Concat(additionalSources.Select((input) =>
            {
                var filePath = getFilePath(input);
                Result<RazorCodeDocument?> codeDocument = new(() => getRazorCodeDocument(filePath, designTime: compilationInput.RazorStrategy == RazorStrategy.DesignTime));

                if (codeDocument.TryGetValue(out var c) && c is null)
                {
                    return KeyValuePair.Create(input.FileName, new CompiledFile([]));
                }

                string razorDiagnostics = codeDocument.Map(c => c?.GetCSharpDocument().GetDiagnostics().JoinToString(Environment.NewLine) ?? "").Serialize();

                var compiledFile = new CompiledFile([
                    new()
                    {
                        Type = "syntax",
                        Label = "Syntax",
                        EagerText = codeDocument.Map(d => d?.GetSyntaxTree().Serialize() ?? "").Serialize(),
                    },
                    new()
                    {
                        Type = "ir",
                        Label = "IR",
                        Language = "csharp",
                        EagerText = codeDocument.Map(d => d?.GetDocumentIntermediateNode().Serialize() ?? "").Serialize(),
                    },
                    .. string.IsNullOrEmpty(razorDiagnostics)
                        ? ImmutableArray<CompiledFileOutput>.Empty
                        : [
                            new()
                            {
                                Type = "razorErrors",
                                Label = "Razor Error List",
                                EagerText = razorDiagnostics,
                            }
                        ],
                    new()
                    {
                        Type = "gcs",
                        Label = "C#",
                        Language = "csharp",
                        EagerText = codeDocument.Map(d => d ?.GetCSharpDocument().GetGeneratedCode() ?? "").Serialize(),
                        Priority = 1,
                    },
                    new()
                    {
                        Type = "html",
                        Label = "HTML",
                        Language = "html",
                        LazyText = new(() =>
                        {
                            var document = codeDocument.Unwrap()?.GetDocumentIntermediateNode()
                                ?? throw new InvalidOperationException("No IR available.");

                            if (document.DocumentKind.StartsWith("mvc"))
                            {
                                throw new InvalidOperationException("Rendering Razor Pages (.cshtml) to HTML is currently not supported. Try Razor Components (.razor) instead.");
                            }

                            if (document.FindPrimaryNamespace() is not { } primaryNamespace)
                            {
                                throw new InvalidOperationException("Cannot find primary namespace.");
                            }

                            if (document.FindPrimaryClass() is not { } primaryClass)
                            {
                                throw new InvalidOperationException("Cannot find primary class.");
                            }

                            var ns = primaryNamespace.Content;
                            var cls = primaryClass.ClassName;

                            if (string.IsNullOrEmpty(cls))
                            {
                                throw new InvalidOperationException("Primary class name is empty.");
                            }

                            var componentTypeName = string.IsNullOrEmpty(ns) ? cls : $"{ns}.{cls}";

                            ValueTask<string> result = tryGetEmitStream(finalCompilation, out var emitStream, out var error)
                                ? new(Executor.RenderComponentToHtmlAsync(emitStream, componentTypeName))
                                : new(error);
                            return result;
                        }),
                    },
                ]);

                return KeyValuePair.Create(input.FileName, compiledFile);
            })).ToImmutableSortedDictionary(static p => p.Key, static p => p.Value),
            NumWarnings: numWarnings,
            NumErrors: numErrors,
            Diagnostics: diagnosticData,
            GlobalOutputs:
            [
                new()
                {
                    Type = "il",
                    Label = "IL",
                    Language = "csharp",
                    LazyText = new(() =>
                    {
                        peFile ??= getPeFile(finalCompilation);
                        return new(getIl(peFile));
                    }),
                },
                new()
                {
                    Type = "seq",
                    Label = "Sequence points",
                    LazyText = new(async () =>
                    {
                        peFile ??= getPeFile(finalCompilation);
                        return await getSequencePoints(peFile);
                    }),
                },
                new()
                {
                    Type = "cs",
                    Label = "C#",
                    Language = "csharp",
                    LazyText = new(async () =>
                    {
                        peFile ??= getPeFile(finalCompilation);
                        return await getCSharpAsync(peFile);
                    }),
                },
                new()
                {
                    Type = "run",
                    Label = "Run",
                    LazyText = new(() =>
                    {
                        string output = tryGetEmitStream(getExecutableCompilation(), out var emitStream, out var error)
                            ? Executor.Execute(emitStream)
                            : error;
                        return new(output);
                    }),
                    Priority = 1,
                },
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

        void executeConfiguration(string code)
        {
            var configCompilation = CSharpCompilation.Create(
                assemblyName: "Configuration",
                syntaxTrees:
                [
                    CSharpSyntaxTree.ParseText(code, parseOptions, "Configuration.cs", Encoding.UTF8),
                    CSharpSyntaxTree.ParseText("""
                        global using DotNetLab;
                        global using Microsoft.CodeAnalysis;
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
            options = Config.CurrentCSharpCompilationOptions;

            logger.LogDebug("Using language version {LangVersion} (specified {SpecifiedLangVersion})", parseOptions.LanguageVersion, parseOptions.SpecifiedLanguageVersion);
        }

        static CSharpCompilationOptions createCompilationOptions(OutputKind outputKind)
        {
            return new CSharpCompilationOptions(
                outputKind,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable,
                concurrentBuild: false);
        }

        static string getFilePath(InputCode input) => directory + input.FileName;

        static bool isCSharp(InputCode input, out bool script)
        {
            return (script = ".csx".Equals(input.FileExtension, StringComparison.OrdinalIgnoreCase)) ||
                ".cs".Equals(input.FileExtension, StringComparison.OrdinalIgnoreCase);
        }

        (CSharpCompilation FinalCompilation, ImmutableArray<Diagnostic> AdditionalDiagnostics) runRazorSourceGenerator()
        {
            var additionalTextsBuilder = ImmutableArray.CreateBuilder<AdditionalText>();

            foreach (var input in additionalSources)
            {
                var filePath = getFilePath(input);
                additionalTextsBuilder.Add(new TestAdditionalText(text: input.Text, encoding: Encoding.UTF8, path: filePath));
                optionsProvider.AdditionalTextOptions[filePath] = new TestAnalyzerConfigOptions
                {
                    ["build_metadata.AdditionalFiles.TargetPath"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(filePath)),
                };

                // If this Razor file has a corresponding CSS file, enable scoping (CSS isolation).
                if (isRazorOrCshtml(input))
                {
                    string cssFileName = input.FileName + ".css";
                    if (additionalSources.Any(c => c.FileName.Equals(cssFileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        optionsProvider.AdditionalTextOptions[filePath]["build_metadata.AdditionalFiles.CssScope"] =
                            RazorUtil.GenerateScope(projectName, filePath);
                    }
                }
            }

            var initialCompilation = CSharpCompilation.Create(
                assemblyName: projectName,
                syntaxTrees: cSharpSources.Select(static s => s.SyntaxTree),
                references: references,
                options: options);

            if (generatorDriver is null)
            {
                generatorDriver = CSharpGeneratorDriver.Create(
                    generators: [new RazorSourceGenerator().AsSourceGenerator()],
                    additionalTexts: additionalTextsBuilder.ToImmutable(),
                    parseOptions: parseOptions,
                    optionsProvider: optionsProvider);
            }
            else
            {
                generatorDriver = generatorDriver
                    .ReplaceAdditionalTexts(additionalTextsBuilder.ToImmutable())
                    .WithUpdatedParseOptions(parseOptions)
                    .WithUpdatedAnalyzerConfigOptions(optionsProvider);
            }

            generatorDriver = (CSharpGeneratorDriver)generatorDriver.RunGeneratorsAndUpdateCompilation(
                initialCompilation,
                out var finalCommonCompilation,
                out var generatorDiagnostics);

            razorResult = generatorDriver.GetRunResult().Results.FirstOrDefault();

            var finalCompilation = (CSharpCompilation)finalCommonCompilation;

            return (finalCompilation, generatorDiagnostics);
        }

        (CSharpCompilation FinalCompilation, ImmutableArray<Diagnostic> AdditionalDiagnostics) runRazorInternalApi()
        {
            var fileSystem = new VirtualRazorProjectFileSystemProxy();
            foreach (var input in additionalSources)
            {
                if (isRazorOrCshtml(input))
                {
                    var filePath = getFilePath(input);
                    var item = RazorAccessors.CreateSourceGeneratorProjectItem(
                        basePath: "/",
                        filePath: filePath,
                        relativePhysicalPath: input.FileName,
                        fileKind: null!, // will be automatically determined from file path
                        additionalText: new TestAdditionalText(input.Text, encoding: Encoding.UTF8, path: filePath),
                        cssScope: null);
                    fileSystem.Add(item);
                }
            }

            var cSharpSyntaxTrees = cSharpSources.Select(static s => s.SyntaxTree);

            var config = RazorConfiguration.Default;

            // Phase 1: Declaration only (to be used as a reference from which tag helpers will be discovered).
            RazorProjectEngine declarationProjectEngine = createProjectEngine([]);
            var declarationCompilation = CSharpCompilation.Create("TestAssembly",
                syntaxTrees: [
                    .. fileSystem.Inner.EnumerateItemsSafe("/").Select((item) =>
                    {
                        RazorCodeDocument declarationCodeDocument = declarationProjectEngine.ProcessDeclarationOnlySafe(item);
                        string declarationCSharp = declarationCodeDocument.GetCSharpDocument().GetGeneratedCode();
                        return CSharpSyntaxTree.ParseText(declarationCSharp, parseOptions, encoding: Encoding.UTF8);
                    }),
                    .. cSharpSyntaxTrees,
                ],
                references,
                options);

            // Phase 2: Full generation.
            var projectEngine = createProjectEngine([
                .. references,
                declarationCompilation.ToMetadataReference()
            ]);
            var allRazorDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            razorMap = fileSystem.Inner.EnumerateItemsSafe("/")
                .ToImmutableDictionary(
                    keySelector: static (item) => item.FilePath,
                    elementSelector: (item) =>
                    {
                        RazorCodeDocument codeDocument = projectEngine.ProcessSafe(item);
                        RazorCodeDocument designTimeDocument = projectEngine.ProcessDesignTimeSafe(item);

                        allRazorDiagnostics.AddRange(codeDocument.GetCSharpDocument().GetDiagnostics().Select(RazorUtil.ToDiagnostic));

                        return (codeDocument, designTimeDocument);
                    });

            var finalCompilation = CSharpCompilation.Create("TestAssembly",
                [
                    .. razorMap.Values.Select((docs) =>
                    {
                        var cSharpText = docs.Runtime.GetCSharpDocument().GetGeneratedCode();
                        return CSharpSyntaxTree.ParseText(cSharpText, parseOptions, encoding: Encoding.UTF8);
                    }),
                    .. cSharpSyntaxTrees,
                ],
                references,
                options);

            return (finalCompilation, allRazorDiagnostics.ToImmutable());

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

                    b.ConfigureRazorParserOptionsSafe(options =>
                    {
                        if (options.GetType().GetProperty("UseRoslynTokenizer") is { } useRoslynTokenizerProperty)
                        {
                            var useRoslynTokenizer = parseOptions.Features.TryGetValue("use-roslyn-tokenizer", out var useRoslynTokenizerValue) &&
                                string.Equals(useRoslynTokenizerValue, bool.TrueString, StringComparison.OrdinalIgnoreCase);
                            useRoslynTokenizerProperty.SetValue(options, useRoslynTokenizer);
                        }

                        if (options.GetType().GetProperty("CSharpParseOptions") is { } cSharpParseOptionsProperty)
                        {
                            cSharpParseOptionsProperty.SetValue(options, parseOptions);
                        }
                    });

                    CompilerFeatures.Register(b);
                    RazorExtensions.Register(b);

                    b.SetCSharpLanguageVersionSafe(LanguageVersion.Preview);
                });
            }
        }

        static bool isRazorOrCshtml(InputCode input)
        {
            return ".razor".Equals(input.FileExtension, StringComparison.OrdinalIgnoreCase) ||
                ".cshtml".Equals(input.FileExtension, StringComparison.OrdinalIgnoreCase);
        }

        RazorCodeDocument? getRazorCodeDocument(string filePath, bool designTime)
        {
            return compilationInput.RazorToolchain switch
            {
                RazorToolchain.SourceGenerator => designTime
                    ? throw new NotSupportedException("Cannot use source generator to obtain design-time internals." + ToolchainHelpText)
                    : getSourceGeneratorRazorCodeDocument(filePath, throwIfUnsupported: true),
                RazorToolchain.InternalApi => getInternalApiRazorCodeDocument(filePath, designTime),
                RazorToolchain.SourceGeneratorOrInternalApi => designTime
                    ? getInternalApiRazorCodeDocument(filePath, designTime)
                    : (getSourceGeneratorRazorCodeDocument(filePath, throwIfUnsupported: false) ?? getInternalApiRazorCodeDocument(filePath, designTime)),
                _ => throw new InvalidOperationException($"Invalid Razor toolchain '{compilationInput.RazorToolchain}'."),
            };
        }

        RazorCodeDocument? getSourceGeneratorRazorCodeDocument(string filePath, bool throwIfUnsupported)
        {
            if (razorResult.TryGetHostOutputSafe("RazorGeneratorResult", out var hostOutput) &&
                hostOutput is not null)
            {
                if (new RazorGeneratorResultSafe(hostOutput).TryGetCodeDocument(filePath, out var codeDocument))
                {
                    return codeDocument;
                }

                return null;
            }

            if (throwIfUnsupported)
            {
                throw new NotSupportedException("The selected version of Razor source generator does not support obtaining information about Razor internals." + ToolchainHelpText);
            }

            return null;
        }

        RazorCodeDocument? getInternalApiRazorCodeDocument(string filePath, bool designTime)
        {
            if (razorMap != null && razorMap.TryGetValue(filePath, out var docs))
            {
                return designTime ? docs.DesignTime : docs.Runtime;
            }

            return null;
        }

        CSharpCompilation getExecutableCompilation()
        {
            return finalCompilation.Options.OutputKind == OutputKind.ConsoleApplication
                ? finalCompilation
                : finalCompilation.WithOptions(finalCompilation.Options.WithOutputKind(OutputKind.ConsoleApplication));
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

        bool tryGetEmitStream(CSharpCompilation compilation,
            [NotNullWhen(returnValue: true)] out MemoryStream? emitStream,
            [NotNullWhen(returnValue: false)] out string? error)
        {
            emitStream = getEmitStream(compilation);
            if (emitStream is null)
            {
                error = compilation.GetDiagnostics().FirstOrDefault(d => d.Id == "CS5001") is { } d
                    ? d.GetMessage(CultureInfo.InvariantCulture)
                    : "Cannot execute due to compilation errors.";
                return false;
            }

            error = null;
            return true;
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
        async Task<string> getSequencePoints(ICSharpCode.Decompiler.Metadata.PEFile? peFile)
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

        async Task<string> getCSharpAsync(ICSharpCode.Decompiler.Metadata.PEFile? peFile)
        {
            if (peFile is null)
            {
                return "";
            }

            var decompiler = await getCSharpDecompilerAsync(peFile);
            return decompiler.DecompileWholeModuleAsString();
        }

        async Task<ICSharpCode.Decompiler.CSharp.CSharpDecompiler> getCSharpDecompilerAsync(ICSharpCode.Decompiler.Metadata.PEFile peFile)
        {
            return new ICSharpCode.Decompiler.CSharp.CSharpDecompiler(
                await getCSharpDecompilerTypeSystemAsync(peFile),
                getCSharpDecompilerSettings());
        }

        async Task<ICSharpCode.Decompiler.TypeSystem.DecompilerTypeSystem> getCSharpDecompilerTypeSystemAsync(ICSharpCode.Decompiler.Metadata.PEFile peFile)
        {
            return await ICSharpCode.Decompiler.TypeSystem.DecompilerTypeSystem.CreateAsync(
                peFile,
                new DecompilerAssemblyResolver(decompilerAssemblyResolverLogger, referenceInfos));
        }

        static ICSharpCode.Decompiler.DecompilerSettings getCSharpDecompilerSettings()
        {
            return new ICSharpCode.Decompiler.DecompilerSettings(ICSharpCode.Decompiler.CSharp.LanguageVersion.CSharp1);
        }
    }
}

public sealed class DecompilerAssemblyResolver(ILogger<DecompilerAssemblyResolver> logger, ImmutableArray<ReferenceInfo> references) : ICSharpCode.Decompiler.Metadata.IAssemblyResolver
{
    public Task<ICSharpCode.Decompiler.Metadata.MetadataFile?> ResolveAsync(ICSharpCode.Decompiler.Metadata.IAssemblyReference reference)
    {
        return Task.FromResult(Resolve(reference));
    }

    public ICSharpCode.Decompiler.Metadata.MetadataFile? Resolve(ICSharpCode.Decompiler.Metadata.IAssemblyReference reference)
    {
        foreach (var r in references)
        {
            if (withoutExtension(r.FileName).Equals(reference.Name, StringComparison.OrdinalIgnoreCase))
            {
                var peReader = new PEReader(ImmutableCollectionsMarshal.AsImmutableArray(r.ImageBytes));
                return new ICSharpCode.Decompiler.Metadata.PEFile(r.FileName, peReader);
            }
        }

        logger.LogError("Cannot resolve assembly '{Name}'.", reference.Name);
        return null;

        static ReadOnlySpan<char> withoutExtension(ReadOnlySpan<char> fileName)
        {
            int index = fileName.LastIndexOf('.');
            return index < 0 ? fileName : fileName[..index];
        }
    }

    public Task<ICSharpCode.Decompiler.Metadata.MetadataFile?> ResolveModuleAsync(ICSharpCode.Decompiler.Metadata.MetadataFile mainModule, string moduleName)
    {
        return Task.FromResult(ResolveModule(mainModule, moduleName));
    }

    public ICSharpCode.Decompiler.Metadata.MetadataFile? ResolveModule(ICSharpCode.Decompiler.Metadata.MetadataFile mainModule, string moduleName)
    {
        logger.LogError("Module resolving not implemented ({ModuleName}).", moduleName);
        return null;
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

internal sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
{
    public override TestAnalyzerConfigOptions GlobalOptions { get; } = new TestAnalyzerConfigOptions();

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => throw new NotImplementedException();

    public Dictionary<string, TestAnalyzerConfigOptions> AdditionalTextOptions { get; } = new();

    public override TestAnalyzerConfigOptions GetOptions(AdditionalText textFile)
    {
        if (!AdditionalTextOptions.TryGetValue(textFile.Path, out var options))
        {
            options = new TestAnalyzerConfigOptions();
            AdditionalTextOptions[textFile.Path] = options;
        }

        return options;
    }
}

internal sealed class TestAnalyzerConfigOptions : AnalyzerConfigOptions
{
    public Dictionary<string, string> Options { get; } = new(KeyComparer);

    public string this[string name]
    {
        get => Options[name];
        set => Options[name] = value;
    }

    public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
        => Options.TryGetValue(key, out value);
}

internal static class Result
{
    public static string Serialize(this Result<string> result)
    {
        return result.TryGetValueOrException(out var value, out var exception)
            ? value
            : exception.SourceException.ToString();
    }
}

internal readonly struct Result<T>
{
    private readonly ExceptionDispatchInfo? _exception;
    private readonly T? _value;

    public Result(ExceptionDispatchInfo exception)
    {
        _exception = exception;
        _value = default;
    }

    public Result(T value)
    {
        _exception = null;
        _value = value;
    }

    public Result(Func<T> factory)
    {
        try
        {
            _exception = null;
            _value = factory();
        }
        catch (Exception ex)
        {
            _exception = ExceptionDispatchInfo.Capture(ex);
            _value = default;
        }
    }

    public T Unwrap()
    {
        if (_exception is { } exception)
        {
            exception.Throw();
            throw exception.SourceException; // unreachable
        }

        return _value!;
    }

    public bool TryGetValue([NotNullWhen(returnValue: true)] out T? value)
    {
        return TryGetValueOrException(out value, out _);
    }

    public bool TryGetValueOrException(
        [NotNullWhen(returnValue: true)] out T? value,
        [NotNullWhen(returnValue: false)] out ExceptionDispatchInfo? exception)
    {
        if (_exception is { } ex)
        {
            value = default;
            exception = ex;
            return false;
        }

        value = _value!;
        exception = null;
        return true;
    }

    public Result<R> Map<R>(Func<T, R> mapper)
    {
        var value = _value;
        return _exception is { } exception
            ? new Result<R>(exception)
            : new Result<R>(() => mapper(value!));
    }
}
