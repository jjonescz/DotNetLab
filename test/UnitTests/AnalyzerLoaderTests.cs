using System.Runtime.InteropServices;
using System.Runtime.Loader;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting.Combinatorial;

namespace DotNetLab;

[TestClass]
public sealed class AnalyzerLoaderTests
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    public void SdkAnalyzerAssemblies_IncludesRegexGenerator()
    {
        SdkAnalyzerAssemblies.All.Should().Contain(static a =>
            a.Name == "System.Text.RegularExpressions.Generator");
    }

    [TestMethod, CombinatorialData]
    public void Load_HandlesGeneratorDependencies(bool includeDependency)
    {
        var helperBytes = Emit("Helper", """
            public class HelperType;
            """);

        var generatorBytes = Emit("DependantGenerator", """
            using Microsoft.CodeAnalysis;

            [Generator]
            public class DependantGenerator : HelperType, IIncrementalGenerator
            {
                public void Initialize(IncrementalGeneratorInitializationContext context) { }
            }
            """, extraRefs:
            [
                MetadataReference.CreateFromFile(typeof(IIncrementalGenerator).Assembly.Location),
                MetadataReference.CreateFromImage(helperBytes),
            ]);

        // The dependent is listed first so discovery must wait until all assemblies are loaded.
        ImmutableArray<RefAssembly> analyzers =
        [
            ToRef("DependantGenerator", generatorBytes),
            .. includeDependency ? [ToRef("Helper", helperBytes)] : ImmutableArray<RefAssembly>.Empty,
        ];

        var alc = new AssemblyLoadContext(nameof(Load_HandlesGeneratorDependencies), isCollectible: true);
        try
        {
            var reference = AnalyzerLoader.Load(alc, analyzers, NullLogger.Instance, out var diagnostics);

            if (includeDependency)
            {
                diagnostics.Should().BeEmpty();
                reference.GetGenerators(LanguageNames.CSharp).Should().ContainSingle();
            }
            else
            {
                diagnostics.Should().Contain(d => d.GetMessage().Contains("Helper", StringComparison.Ordinal));
                reference.GetGenerators(LanguageNames.CSharp).Should().BeEmpty();
            }
        }
        finally
        {
            alc.Unload();
        }
    }
    
    [TestMethod]
    public void Load_SameNameAndVersion_InstantiatesGeneratorOnce()
    {
        var generatorBytes = Emit("SampleGenerator", """
             using Microsoft.CodeAnalysis;

             [Generator]
             public class SampleGenerator : IIncrementalGenerator
             {
                 public void Initialize(IncrementalGeneratorInitializationContext context) { }
             }
             """, extraRefs:
            [
                MetadataReference.CreateFromFile(typeof(IIncrementalGenerator).Assembly.Location),
            ]);

        // Same assembly twice: a #:package copy followed by the embedded BCL copy.
        var analyzers = ImmutableArray.Create(
            ToRef("SampleGenerator", generatorBytes),
            ToRef("SampleGenerator", generatorBytes));

        var alc = new AssemblyLoadContext(nameof(Load_SameNameAndVersion_InstantiatesGeneratorOnce), isCollectible: true);
        try
        {
            var reference = AnalyzerLoader.Load(alc, analyzers, NullLogger.Instance, out var diagnostics);

            diagnostics.Should().BeEmpty();
            reference.GetGenerators(LanguageNames.CSharp).Should().ContainSingle();
        }
        finally
        {
            alc.Unload();
        }
    }

    [TestMethod]
    public void Load_DiscoversCSharpAnalyzersAndReportsConstructionFailures()
    {
        var bytes = Emit("SampleAnalyzers", """
            using System;
            using System.Collections.Immutable;
            using Microsoft.CodeAnalysis;
            using Microsoft.CodeAnalysis.Diagnostics;

            [DiagnosticAnalyzer(LanguageNames.CSharp)]
            public class SampleAnalyzer : DiagnosticAnalyzer
            {
                public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [];
                public override void Initialize(AnalysisContext context) { }
            }

            [DiagnosticAnalyzer(LanguageNames.VisualBasic)]
            public sealed class VisualBasicAnalyzer : SampleAnalyzer
            {
                public VisualBasicAnalyzer() => throw new InvalidOperationException("Wrong language");
            }

            [DiagnosticAnalyzer(LanguageNames.CSharp)]
            public sealed class BrokenAnalyzer : SampleAnalyzer
            {
                public BrokenAnalyzer() => throw new InvalidOperationException("Analyzer construction failed");
            }
            """, extraRefs:
            [
                MetadataReference.CreateFromFile(typeof(IIncrementalGenerator).Assembly.Location),
            ]);

        var alc = new AssemblyLoadContext(nameof(Load_DiscoversCSharpAnalyzersAndReportsConstructionFailures), isCollectible: true);
        try
        {
            var reference = AnalyzerLoader.Load(alc, [ToRef("SampleAnalyzers", bytes), ToRef("SampleAnalyzers", bytes)],
                NullLogger.Instance, out var diagnostics);

            reference.GetAnalyzers(LanguageNames.CSharp).Should().ContainSingle()
                .Which.GetType().Name.Should().Be("SampleAnalyzer");
            reference.GetAnalyzers(LanguageNames.VisualBasic).Should().BeEmpty();
            diagnostics.Should().ContainSingle().Which.GetMessage().Should()
                .Contain("BrokenAnalyzer").And.Contain("Analyzer construction failed");
        }
        finally
        {
            alc.Unload();
        }
    }

    /// <summary>
    /// Emits a C# assembly with the given name and source code, returning the bytes of the resulting DLL.
    /// </summary>
    /// <param name="assemblyName"></param>
    /// <param name="source"></param>
    /// <param name="extraRefs"></param>
    /// <returns></returns>
    private ImmutableArray<byte> Emit(
        string assemblyName,
        string source,
        ImmutableArray<PortableExecutableReference>? extraRefs = null)
    {
        var refs = extraRefs is { } extra
            ? RefAssemblyMetadata.All.AddRange(extra)
            : RefAssemblyMetadata.All;

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
        {
            TestContext.WriteLine(string.Join("\n", result.Diagnostics));
        }

        result.Success.Should().BeTrue();
        return ImmutableCollectionsMarshal.AsImmutableArray(stream.ToArray());
    }

    /// <summary>
    /// Creates a <see cref="RefAssembly"/> from the given name and bytes.
    /// </summary>
    /// <param name="name"></param>
    /// <param name="bytes"></param>
    /// <returns></returns>
    private static RefAssembly ToRef(string name, ImmutableArray<byte> bytes) => new()
    {
        Name = name,
        FileName = name + ".dll",
        Bytes = bytes,
        Source = "Test",
    };
}
