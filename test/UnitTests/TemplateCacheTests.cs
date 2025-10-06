using AwesomeAssertions;
using DotNetLab.Lab;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace DotNetLab;

public sealed class TemplateCacheTests(ITestOutputHelper output)
{
    private static readonly TemplateCache cache = new();

    [Fact]
    public void NumberOfEntries()
    {
        var count = 3;
        Assert.Equal(count, cache.Entries.Length);
        Assert.Equal(count, GetKeys().Distinct().Count());
    }

    [Theory, MemberData(nameof(GetKeys))]
    public async Task UpToDate(string key)
    {
        var entry = cache.Entries.Single(e => e.Name == key);
        var (_, input, embeddedJsonFactory) = entry;

        // Compile to get the output corresponding to a template input.
        var services = WorkerServices.CreateTest();
        var compiler = services.GetRequiredService<CompilerProxy>();
        var actualOutput = await compiler.CompileAsync(input);

        // Expand all lazy outputs.
        var allOutputs = actualOutput.Files.Values.SelectMany(f => f.Outputs)
            .Concat(actualOutput.GlobalOutputs);
        foreach (var output in allOutputs)
        {
            string? value;
            try
            {
                value = (await output.LoadAsync(outputFactory: null)).Text;
            }
            catch (Exception ex)
            {
                value = $"{ex.GetType()}: {ex.Message}";
                output.SetEagerText(value);
            }

            Assert.NotNull(value);
            Assert.Equal(value, output.Text);
        }

        Assert.True(cache.TryGetOutput(SavedState.From(input), out _, out var expectedOutput));

        // Code generation depends on system new line sequence, so continue only on systems where new line is '\n'.
        if (Environment.NewLine is not "\n")
        {
            Assert.Skip($"""
                Only "\n" is supported, got {SymbolDisplay.FormatPrimitive(Environment.NewLine, quoteStrings: true, useHexadecimalNumbers: false)}.
                """);
        }

        var snapshotDirectory = Path.GetFullPath(Path.Join(TestContext.Current.TestAssembly!.AssemblyPath,
            "..", "..", "..", "..", "..", "eng", "Analyzers", "snapshots"));

        // Compare on-disk snapshot with expected snapshot from memory.
        var actualNode = JsonSerializer.SerializeToNode(actualOutput, WorkerJsonContext.Default.CompiledAssembly)!.AsObject();
        var actualSnapshot = Snapshot.LoadFromJson(key, actualNode);
        var expectedSnapshot = Snapshot.LoadFromDisk(key, snapshotDirectory);
        bool match = expectedSnapshot.Equals(actualSnapshot);
        if (!match)
        {
            // If snapshots don't match, regenerate.
            output.WriteLine($"Regenerating snapshot '{expectedSnapshot}' -> '{actualSnapshot}' at '{snapshotDirectory}'.");
            actualSnapshot.SaveToDisk(snapshotDirectory);
        }
        else
        {
            output.WriteLine($"Expected snapshot '{expectedSnapshot}' at '{snapshotDirectory}' matches actual '{actualSnapshot}'.");
        }

        // Check that snapshot API works.
        var actualSnapshotJson = actualSnapshot.ToJson().ToJsonString();
        var expectedSnapshotJson = expectedSnapshot.ToJson().ToJsonString();
        actualSnapshotJson.Should().Be(expectedSnapshotJson);

        // Compare JSONs.
        var embeddedJson = Encoding.UTF8.GetString(embeddedJsonFactory());
        embeddedJson.Should().Be(actualSnapshotJson);

        // Compare the objects.
        actualOutput.Should().BeEquivalentTo(expectedOutput);

        // Do this last because errors from above assertions would be better.
        match.Should().BeTrue();
    }

    public static TheoryData<string> GetKeys()
    {
        return new(cache.Entries.Select(static entry => entry.Name));
    }
}
