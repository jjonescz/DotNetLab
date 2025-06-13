using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.CodeAnalysis;
using Microsoft.NET.Sdk.Razor.SourceGenerators;
using System.Runtime.CompilerServices;

namespace DotNetLab;

public static class RazorAccessors
{
    public static RazorProjectItem CreateSourceGeneratorProjectItem(
        string basePath,
        string filePath,
        string relativePhysicalPath,
        AdditionalText additionalText,
        string? cssScope)
    {
        var ctor = typeof(SourceGeneratorProjectItem).GetConstructors().First();
        object? fileKind = ctor.GetParameters()[3].ParameterType.IsEnum
            ? GetFileKindFromPath(additionalText.Path)
            : null; // will be automatically determined from file path

        return (RazorProjectItem)ctor.Invoke([
            /* basePath: */ basePath,
            /* filePath: */ filePath,
            /* relativePhysicalPath: */ relativePhysicalPath,
            /* fileKind: */ fileKind,
            /* additionalText: */ additionalText,
            /* cssScope: */ cssScope]);
    }

    /// <summary>
    /// Wrapper to avoid <see cref="MissingMethodException"/>s in the caller during JITing
    /// even though the method is not actually called.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object GetFileKindFromPath(string filePath)
    {
        return FileKinds.GetFileKindFromPath(filePath);
    }

    public static string Serialize(this DocumentIntermediateNode node)
    {
        var formatter = new DebuggerDisplayFormatter();
        formatter.FormatTree(node);
        return formatter.ToString();
    }

    public static string Serialize(this RazorSyntaxTree tree)
    {
        // SerializedValue property has been moved to another type in https://github.com/dotnet/razor/pull/11859.

        var root = tree.Root;

        // For some reason, the property might not be returned by reflection if inherited, so look manually into the base type.
        const string propName = "SerializedValue";
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var rootType = root.GetType();
        var prop = rootType.GetProperty(propName, flags)
            ?? rootType.BaseType!.GetProperty(propName, flags)!;

        return (string)prop.GetValue(root)!;
    }
}

public sealed class VirtualRazorProjectFileSystemProxy
{
    private readonly VirtualRazorProjectFileSystem inner = new();

    public RazorProjectFileSystem Inner => inner;

    public void Add(RazorProjectItem item) => inner.Add(item);
}
