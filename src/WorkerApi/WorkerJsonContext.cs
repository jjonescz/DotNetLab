using BlazorMonaco.Editor;
using DotNetLab.Lab;
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

    private static MethodInfo GetTypeInfoBaseMethod
    {
        get
        {
            return field ??= getNoCache();

            static MethodInfo getNoCache()
            {
                var interfaceMap = typeof(WorkerJsonContext).BaseType!.GetInterfaceMap(typeof(IJsonTypeInfoResolver));
                var interfaceMethod = typeof(IJsonTypeInfoResolver).GetMethod(nameof(IJsonTypeInfoResolver.GetTypeInfo))!;
                var index = interfaceMap.InterfaceMethods.IndexOf(interfaceMethod);
                return interfaceMap.TargetMethods[index];
            }
        }
    }

    JsonTypeInfo? IJsonTypeInfoResolver.GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = (JsonTypeInfo?)GetTypeInfoBaseMethod.Invoke(this, [type, options]);

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
