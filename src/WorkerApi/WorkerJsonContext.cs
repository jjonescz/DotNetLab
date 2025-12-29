using BlazorMonaco.Editor;
using DotNetLab.Lab;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DotNetLab;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(WorkerInputMessage))]
[JsonSerializable(typeof(WorkerOutputMessage))]
[JsonSerializable(typeof(CompiledAssembly))]
[JsonSerializable(typeof(CompiledFileLazyResult))]
[JsonSerializable(typeof(PackageDependencyInfo))]
[JsonSerializable(typeof(List<SdkVersionInfo>))]
[JsonSerializable(typeof(SdkInfo))]
[JsonSerializable(typeof(ImmutableArray<MarkerData>))]
[JsonSerializable(typeof(PingResult))]
[Obsolete($"Use {nameof(WorkerJsonContext)} instead.")]
public partial class WorkerJsonContextBase : JsonSerializerContext
{
    protected internal static JsonSerializerOptions DefaultOptions => s_defaultOptions;
}

public sealed class WorkerJsonContext
#pragma warning disable CS0618 // Type or member is obsolete
    : WorkerJsonContextBase
#pragma warning restore CS0618
    , IJsonTypeInfoResolver
{
    public WorkerJsonContext() { }

    public WorkerJsonContext(JsonSerializerOptions options) : base(options) { }

    public new static WorkerJsonContext Default { get; } = new(new(DefaultOptions));

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "global::System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver.GetTypeInfo")]
    private static extern JsonTypeInfo? GetTypeInfoBase(
#pragma warning disable CS0618 // Type or member is obsolete
        WorkerJsonContextBase @this,
#pragma warning restore CS0618
        Type type,
        JsonSerializerOptions options);

    JsonTypeInfo? IJsonTypeInfoResolver.GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = GetTypeInfoBase(this, type, options);

        if (typeInfo is null)
        {
            return null;
        }

        foreach (var prop in typeInfo.Properties)
        {
            prop.IsRequired = false;
        }

        return typeInfo;
    }
}
