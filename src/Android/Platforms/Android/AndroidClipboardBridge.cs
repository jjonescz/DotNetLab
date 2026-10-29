using Android.Content;
using Android.Webkit;
using Java.Interop;

namespace DotNetLab;

public sealed class AndroidClipboardBridge : Java.Lang.Object
{
    public const string Name = "__dotNetLabClipboard";

    [JavascriptInterface]
    [Export("readText")]
    public string ReadText()
    {
        var clipboard = GetClipboardManager();
        return clipboard.PrimaryClip?.GetItemAt(0)?.CoerceToText(Android.App.Application.Context)?.ToString() ?? "";
    }

    [JavascriptInterface]
    [Export("writeText")]
    public void WriteText(string? text)
    {
        var clipboard = GetClipboardManager();
        clipboard.PrimaryClip = ClipData.NewPlainText("DotNetLab", text ?? "");
    }

    private static ClipboardManager GetClipboardManager()
    {
        return (ClipboardManager)Android.App.Application.Context.GetSystemService(Context.ClipboardService)!;
    }
}
