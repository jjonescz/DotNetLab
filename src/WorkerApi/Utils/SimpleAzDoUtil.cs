namespace DotNetLab;

public static class SimpleAzDoUtil
{
    public static readonly string BaseAddress = "https://dev.azure.com/dnceng-public/public";

    public static string GetBuildListUrl(int definitionId)
    {
        return $"{BaseAddress}/_build?definitionId={definitionId}";
    }
}
