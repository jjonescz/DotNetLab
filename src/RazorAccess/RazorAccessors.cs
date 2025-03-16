using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.NET.Sdk.Razor.SourceGenerators;

namespace DotNetLab;

public static class RazorAccessors
{
    public static RazorEngineFeatureBase CreateDefaultTypeNameFeature()
        => new DefaultTypeNameFeature();

    public static RazorProjectItem CreateSourceGeneratorProjectItem(
        string basePath,
        string filePath,
        string relativePhysicalPath,
        string fileKind,
        AdditionalText additionalText,
        string? cssScope)
    {
        return new SourceGeneratorProjectItem(
            basePath: basePath,
            filePath: filePath,
            relativePhysicalPath: relativePhysicalPath,
            fileKind: fileKind,
            additionalText: additionalText,
            cssScope: cssScope);
    }

    public static string Serialize(this DocumentIntermediateNode node)
    {
        var formatter = new DebuggerDisplayFormatter();
        formatter.FormatTree(node);
        return formatter.ToString();
    }

    public static string Serialize(this RazorSyntaxTree tree)
    {
        return tree.Root.SerializedValue;
    }
}

public sealed class VirtualRazorProjectFileSystemProxy
{
    private readonly VirtualRazorProjectFileSystem inner = new();

    public RazorProjectFileSystem Inner => inner;

    public void Add(RazorProjectItem item) => inner.Add(item);
}
