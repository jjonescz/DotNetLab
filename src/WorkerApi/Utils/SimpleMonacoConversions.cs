using BlazorMonaco;
using BlazorMonaco.Editor;

namespace DotNetLab;

public static class SimpleMonacoConversions
{
    public static MarkerData ToMarkerData(this DiagnosticData d)
    {
        return new MarkerData
        {
            CodeAsObject = new()
            {
                Value = d.Id,
                TargetUri = d.HelpLinkUri,
            },
            Message = d.Message,
            StartLineNumber = d.StartLineNumber,
            StartColumn = d.StartColumn,
            EndLineNumber = d.EndLineNumber,
            EndColumn = d.EndColumn,
            Severity = d.Severity switch
            {
                DiagnosticDataSeverity.Error => MarkerSeverity.Error,
                DiagnosticDataSeverity.Warning => MarkerSeverity.Warning,
                _ => MarkerSeverity.Info,
            },
        };
    }
}
