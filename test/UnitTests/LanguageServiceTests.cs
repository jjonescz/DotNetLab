using AwesomeAssertions;
using BlazorMonaco;
using DotNetLab.Lab;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace DotNetLab;

[TestClass]
public sealed class LanguageServiceTests
{
    public required TestContext TestContext { get; set; }

    [TestMethod]
    [DataRow("void func() { }", "CS8321", "Remove unused function")]
    [DataRow("Console.WriteLine();", "CS0103", "using System;")]
    [DataRow("{ return 1; return 2; }", "CS0162", "Remove unreachable code")]
    public async Task CodeActions(string code, string expectedErrorCode, string expectedCodeActionTitle)
    {
        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        await languageServices.OnDidChangeWorkspaceAsync([new("test.cs", "test.cs") { NewContent = code }]);

        var markers = await languageServices.GetDiagnosticsAsync("test.cs");
        markers.Should().NotBeEmpty();
        TestContext.WriteLine($"Diagnostics:\n{markers.Select(m => $"{m.Message} ({m.Code})").JoinToString("\n")}");

        var codeActionsJson = await languageServices.ProvideCodeActionsAsync("test.cs", null, TestContext.CancellationToken);
        var codeActions = JsonSerializer.Deserialize(codeActionsJson!, BlazorMonacoJsonContext.Default.ImmutableArrayMonacoCodeAction);

        codeActions.Should().NotBeEmpty();
        TestContext.WriteLine($"Code actions:\n{codeActions.Select(c => $"{c.Title}: {c.Edit?.Edits.Select(e => e.TextEdit.Text).JoinToString(", ") ?? "null"}").JoinToString("\n")}");

        markers.Select(m => m.Code.ToString()).Should().ContainMatch($"*{expectedErrorCode}*");
        codeActions.Select(c => c.Title).Should().Contain(expectedCodeActionTitle);
    }

    [TestMethod]
    public async Task SignatureHelp()
    {
        var services = WorkerServices.CreateTest(TestContext);
        var compiler = services.GetRequiredService<CompilerProxy>();
        var languageServices = await compiler.GetLanguageServicesAsync();
        var code = """
            C.M();
            class C { public static void M(int x) { } }
            """;
        var file = "test.cs";
        await languageServices.OnDidChangeWorkspaceAsync([new(file, file) { NewContent = code }]);

        var positionJson = JsonSerializer.Serialize(new Position { LineNumber = 1, Column = 5 }, BlazorMonacoJsonContext.Default.Position);
        var contextJson = JsonSerializer.Serialize(new SignatureHelpContext { TriggerKind = SignatureHelpTriggerKind.Invoke, IsRetrigger = false }, BlazorMonacoJsonContext.Default.SignatureHelpContext);
        var signatureHelpJson = await languageServices.ProvideSignatureHelpAsync(file, positionJson, contextJson, TestContext.CancellationToken);
        var signatureHelp = JsonSerializer.Deserialize(signatureHelpJson!, BlazorMonacoJsonContext.Default.SignatureHelp);

        signatureHelp!.Signatures.Should().ContainSingle()
            .Which.Label.Should().Be("void C.M(int x)");
    }
}
