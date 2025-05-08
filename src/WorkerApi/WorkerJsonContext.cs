using BlazorMonaco.Editor;
using BlazorMonaco.Languages;
using DotNetLab.Lab;
using System.Text.Json.Serialization;

namespace DotNetLab;

[JsonSerializable(typeof(WorkerInputMessage))]
[JsonSerializable(typeof(WorkerOutputMessage))]
[JsonSerializable(typeof(CompiledAssembly))]
[JsonSerializable(typeof(CompilerDependencyInfo))]
[JsonSerializable(typeof(SdkInfo))]
[JsonSerializable(typeof(ImmutableArray<MarkerData>))]
[JsonSerializable(typeof(CompletionList))]
public sealed partial class WorkerJsonContext : JsonSerializerContext;
