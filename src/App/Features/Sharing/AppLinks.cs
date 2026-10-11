using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Features.Preferences;

namespace DotNetLab.Features.Sharing;

public static class AppLinks
{
    public const string Repository = "https://github.com/jjonescz/DotNetLab";
    public const string Releases = $"{Repository}/releases";
    public const string NativeApps = $"{Repository}/blob/main/docs/native-apps.md";

    public static string NewIssue(CompilerState compiler, PreferencesState prefs, DocumentWorkspace documents)
    {
        var body = $"""
            ### Environment
            - Template: {documents.Template}
            - SDK: {compiler.Sdk}
            - Roslyn: {compiler.Roslyn} ({compiler.RoslynConfig})
            - Razor: {compiler.Razor} ({compiler.RazorConfig})
            - Theme: {prefs.AppTheme}

            ### Description


            """;

        return $"{Repository}/issues/new?title={Uri.EscapeDataString("[.NET Lab] ")}&body={Uri.EscapeDataString(body)}";
    }
}
