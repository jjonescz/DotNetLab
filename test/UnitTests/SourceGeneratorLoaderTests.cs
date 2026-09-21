using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace DotNetLab;

[TestClass]
public sealed class SourceGeneratorLoaderTests
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    public void Load_DiscoversGeneratorWhenDependencyIsListedAfterIt()
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

        // Dependent first — one-pass Load would call GetTypes() before Helper is in the ALC.
        var analyzers = ImmutableArray.Create(
            ToRef("DependantGenerator", generatorBytes),
            ToRef("Helper", helperBytes));

        var alc = new AssemblyLoadContext(nameof(Load_DiscoversGeneratorWhenDependencyIsListedAfterIt), isCollectible: true);
        try
        {
            var generators = SourceGeneratorLoader.Load(alc, analyzers, NullLogger.Instance, out var diagnostics);

            diagnostics.Should().BeEmpty();
            generators.Should().ContainSingle();
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
