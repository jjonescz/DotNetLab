using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetLab.Lab;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ProductCommit))]
[JsonSerializable(typeof(SourceManifest))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LabWorkerJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.KebabCaseLower)]
[JsonSerializable(typeof(DotNetReleaseIndex))]
[JsonSerializable(typeof(DotNetReleaseIndex.ReleaseList))]
internal sealed partial class LabWorkerKebabCaseJsonContext : JsonSerializerContext;
