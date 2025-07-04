using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetLab.Lab;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DotNetBootConfig))]
[JsonSerializable(typeof(ProductCommit))]
[JsonSerializable(typeof(SourceManifest))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LabWorkerJsonContext : JsonSerializerContext;
