using DotNetLab.Lab;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace DotNetLab;

public sealed class TemplateCacheTests
{
    private static readonly TemplateCache cache = new();

    [Fact]
    public void NumberOfEntries()
    {
        Assert.Equal(3, cache.Entries.Length);
    }

    [Theory, MemberData(nameof(GetIndices))]
    public async Task UpToDate(int index)
    {
        var (input, actualJsonFactory) = cache.Entries[index];

        // Compile to get the output corresponding to a template input.
        var services = WorkerServices.CreateTest();
        var compiler = services.GetRequiredService<CompilerProxy>();
        var actualOutput = await compiler.CompileAsync(input);

        // Expand all lazy outputs.
        var allOutputs = actualOutput.Files.Values.SelectMany(f => f.Outputs)
            .Concat(actualOutput.GlobalOutputs);
        foreach (var output in allOutputs)
        {
            await output.GetTextAsync(outputFactory: null);
            Assert.NotNull(output.EagerText);
        }

        // Compare JSONs (do this early so when templates are updated,
        // this fails and can be used to do manual updates of the JSON snapshots).
        var expectedJson = JsonSerializer.Serialize(actualOutput);
        var actualJson = Encoding.UTF8.GetString(actualJsonFactory());
        if (expectedJson != actualJson)
        {
            var fileName = Path.GetTempFileName();
            File.WriteAllText(fileName, expectedJson);
            actualJson.Should().Be(expectedJson,
                $"JSON string literal inside {nameof(TemplateCache)} needs to be updated, see '{fileName}'.");
        }

        // Compare the objects as well.
        Assert.True(cache.TryGetOutput(input, out var expectedOutput));
        actualOutput.Should().BeEquivalentTo(expectedOutput);
    }

    public static TheoryData<int> GetIndices()
    {
        return new(Enumerable.Range(0, cache.Entries.Length));
    }
}
