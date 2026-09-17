namespace DotNetLab.Features.Compiler;

public sealed record SdkOption(string Value, string Label, string Roslyn, string Razor)
{
    public string Released
    {
        get
        {
            const string marker = " — ";
            var index = Label.IndexOf(marker, StringComparison.Ordinal);
            return index >= 0 ? Label[(index + marker.Length)..] : string.Empty;
        }
    }
}
