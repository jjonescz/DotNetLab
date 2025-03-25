using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetLab.Lab;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(BlazorBootJson))]
[JsonSerializable(typeof(ProductCommit))]
internal sealed partial class LabWorkerJsonContext : JsonSerializerContext;
