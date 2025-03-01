namespace DotNetLab;

internal static class StringUtil
{
    public static string GetFirstLine(this string text)
    {
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            return line.ToString();
        }

        return text;
    }

    public static string SeparateThousands(this int number)
    {
        return number.ToString("N0");
    }
}
