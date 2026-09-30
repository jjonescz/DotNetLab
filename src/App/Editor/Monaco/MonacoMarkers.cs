using BlazorMonaco;
using BlazorMonaco.Editor;

namespace DotNetLab.Editor.Monaco;

internal static class MonacoConstants
{
    public static readonly string MarkersOwner = "Lab";
}

internal static class MonacoMarkers
{
    public static MarkerData WithSeverityIcon(this MarkerData markerData)
    {
        var prefix = markerData.Severity switch
        {
            MarkerSeverity.Error => "\u2715 ",
            MarkerSeverity.Warning => "\u26a0\ufe0e ",
            _ => "\u24d8 ",
        };

        markerData.Message = prefix + markerData.Message;
        return markerData;
    }
}
