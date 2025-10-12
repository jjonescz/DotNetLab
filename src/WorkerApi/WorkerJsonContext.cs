using BlazorMonaco.Editor;
using DotNetLab.Lab;
using System.Text.Json.Serialization;

namespace DotNetLab;

[JsonSerializable(typeof(WorkerInputMessage))]
[JsonSerializable(typeof(WorkerOutputMessage))]
[JsonSerializable(typeof(CompiledAssembly))]
[JsonSerializable(typeof(CompiledFileLazyResult))]
[JsonSerializable(typeof(PackageDependencyInfo))]
[JsonSerializable(typeof(List<SdkVersionInfo>))]
[JsonSerializable(typeof(SdkInfo))]
[JsonSerializable(typeof(ImmutableArray<MarkerData>))]
[JsonSerializable(typeof(PingResult))]
public sealed partial class WorkerJsonContext : JsonSerializerContext;
