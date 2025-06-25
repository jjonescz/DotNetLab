using AwesomeAssertions;
using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace DotNetLab;

public sealed class LanguageServiceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("void func() { }", "CS8321", "Remove unused function")]
    [InlineData("Console.WriteLine();", "CS0103", "using System;")]
    [InlineData("{ return 1; return 2; }", "CS0162", "Remove unreachable code")]
    public async Task CodeActions(string code, string expectedErrorCode, string expectedCodeActionTitle)
    {
        var services = WorkerServices.CreateTest();
        var languageServices = services.GetRequiredService<LanguageServices>();
        await languageServices.OnDidChangeWorkspaceAsync([new("test.cs", "test.cs") { NewContent = code }]);
        languageServices.OnDidChangeModel("test.cs");

        var markers = await languageServices.GetDiagnosticsAsync();
        markers.Should().NotBeEmpty();
        output.WriteLine($"Diagnostics:\n{markers.Select(m => $"{m.Message} ({m.Code})").JoinToString("\n")}");

        var codeActionsJson = await languageServices.ProvideCodeActionsAsync("test.cs", null, TestContext.Current.CancellationToken);
        var codeActions = JsonSerializer.Deserialize(codeActionsJson!, BlazorMonacoJsonContext.Default.ImmutableArrayMonacoCodeAction);

        codeActions.Should().NotBeEmpty();
        output.WriteLine($"Code actions:\n{codeActions.Select(c => $"{c.Title}: {c.Edit?.Edits.Select(e => e.TextEdit.Text).JoinToString(", ") ?? "null"}").JoinToString("\n")}");

        markers.Select(m => m.Code.ToString()).Should().ContainMatch($"*{expectedErrorCode}*");
        codeActions.Select(c => c.Title).Should().Contain(expectedCodeActionTitle);
    }
}
