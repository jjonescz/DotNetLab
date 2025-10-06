using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotNetLab;

// RS1035: Do not use APIs banned for analyzers.
// We are using System.IO, but that is only used by tests which this class is shared with.
#pragma warning disable RS1035

/// <summary>
/// Represents a snapshot of an arbitrary object tree which can be represented as a compact JSON or an expanded set of JSON files
/// (with multi-line text fields extracted to separate files for easier reviewability by humans).
/// </summary>
public sealed class Snapshot : IEquatable<Snapshot>
{
    private const string jsonExtension = ".json";
    private const string txtExtension = ".txt";
    private static readonly StringComparer keyComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly IEqualityComparer<ImmutableDictionary<string, string>> filesComparer =
        Comparers.ImmutableDictionary<string, string, Comparers.String.OrdinalIgnoreCase>.EqualityComparer;
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
    };
    private static readonly Func<string, string> readFile = static (file) => File.ReadAllText(file, Encoding.UTF8);

    public static Snapshot LoadFromDisk(string name, string directory)
    {
        var files = !Directory.Exists(directory)
            ? []
            : Directory.EnumerateFiles(directory);

        return LoadFromFiles(name, files.Select(static file => (
            File: file,
            FileName: Path.GetFileName(file),
            Content: readFile)));
    }

    public static Snapshot LoadFromFiles<T>(string name, IEnumerable<(T File, string FileName, Func<T, string> Content)> files)
    {
        var fileSearcher = new FileSearcher(name);

        var builder = ImmutableDictionary.CreateBuilder<string, string>(keyComparer);

        foreach (var (file, fileName, content) in files)
        {
            if (!fileSearcher.IsMatch(fileName))
            {
                continue;
            }

            builder[fileName] = content(file);
        }

        return new Snapshot(name, builder.ToImmutable());
    }

    public static Snapshot LoadFromJson(string name, JsonObject root)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(keyComparer);

        var queue = new Queue<(string Prefix, JsonNode? Node)>();

        foreach (var kvp in root)
        {
            queue.Enqueue(($"{name}.{kvp.Key}", kvp.Value));
        }

        while (queue.TryDequeue(out var item))
        {
            var (prefix, node) = item;
            switch (node)
            {
                case JsonObject obj:
                    foreach (var kvp in obj)
                    {
                        queue.Enqueue(($"{prefix}.{EncodeKeyForFileName(kvp.Key)}", kvp.Value));
                    }
                    break;

                case JsonArray arr:
                    for (int i = 0; i < arr.Count; i++)
                    {
                        queue.Enqueue(($"{prefix}.{i}", arr[i]));
                    }
                    break;

                case JsonValue value:
                    if (value.TryGetValue(out string? s) &&
                        s.Contains('\n'))
                    {
                        var key = $"{prefix}{txtExtension}";
                        builder[key] = s;
                        value.ReplaceWith(key);
                    }
                    break;

                case null:
                    break;

                default:
                    throw new InvalidOperationException($"Unexpected JSON node type: {node.GetType().Name}");
            }
        }

        builder[$"{name}{jsonExtension}"] = root.ToJsonString(jsonOptions);

        return new Snapshot(name, builder.ToImmutable());
    }

    private Snapshot(string name, ImmutableDictionary<string, string> files)
    {
        Debug.Assert(files.KeyComparer == keyComparer);
        Name = name;
        Files = files;
    }

    public string Name { get; }
    public ImmutableDictionary<string, string> Files { get; }

    public void SaveToDisk(string directory)
    {
        Directory.CreateDirectory(directory);

        foreach (var kvp in Files)
        {
            var path = Path.Join(directory, kvp.Key);
            File.WriteAllText(path, kvp.Value, Encoding.UTF8);
        }

        var fileSearcher = new FileSearcher(Name);

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var fileName = Path.GetFileName(file);
            if (!fileSearcher.IsMatch(fileName))
            {
                continue;
            }

            if (!Files.ContainsKey(fileName))
            {
                File.Delete(file);
            }
        }
    }

    public JsonObject ToJson()
    {
        var rootJson = Files[$"{Name}{jsonExtension}"];
        var root = JsonNode.Parse(rootJson)?.AsObject()
            ?? throw new InvalidOperationException($"Unexpected null at '{Name}{jsonExtension}'.");

        var prefix = $"{Name}.";

        foreach (var kvp in Files)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                kvp.Key.EndsWith(txtExtension, StringComparison.OrdinalIgnoreCase))
            {
                var key = kvp.Key[prefix.Length..^txtExtension.Length];
                var keyParts = key.Split('.');
                JsonNode parent = root;
                foreach (var part in keyParts)
                {
                    parent = parent.GetValueKind() switch
                    {
                        JsonValueKind.Object => parent.AsObject()[DecodeKeyFromFileName(part)],
                        JsonValueKind.Array => parent.AsArray()[int.Parse(part)],
                        var valueKind => throw new InvalidOperationException($"Unexpected JSON node type '{valueKind}' in '{key}' at '{part}'."),
                    }
                    ?? throw new InvalidOperationException($"Unexpected null in '{key}' at '{part}'.");
                }
                parent.ReplaceWith(kvp.Value);
            }
        }

        return root;
    }

    private static string EncodeKeyForFileName(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var ch in key)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append($"_{(int)ch:X4}_");
            }
        }
        return builder.ToString();
    }

    private static string DecodeKeyFromFileName(string fileName)
    {
        var builder = new StringBuilder(fileName.Length);
        for (int i = 0; i < fileName.Length; i++)
        {
            var ch = fileName[i];
            if (ch == '_' &&
                i + 5 < fileName.Length &&
                int.TryParse(fileName.AsSpan(i + 1, 4), NumberStyles.HexNumber, null, out int code) &&
                fileName[i + 5] == '_')
            {
                builder.Append((char)code);
                i += 5;
            }
            else
            {
                builder.Append(ch);
            }
        }
        return builder.ToString();
    }

    public bool Equals(Snapshot? other)
    {
        return filesComparer.Equals(Files, other?.Files);
    }

    public override bool Equals(object? obj)
    {
        return obj is Snapshot other && Equals(other);
    }

    public override int GetHashCode()
    {
        return filesComparer.GetHashCode(Files);
    }

    public override string ToString()
    {
        return $"{Name} ({Files.Count})";
    }

    private readonly struct FileSearcher(string name)
    {
        private readonly string JsonFileName = $"{name}{jsonExtension}";
        private readonly string TxtFilePrefix = $"{name}.";

        public bool IsMatch(string fileName)
        {
            return fileName.Equals(JsonFileName, StringComparison.OrdinalIgnoreCase) ||
                (fileName.StartsWith(TxtFilePrefix, StringComparison.OrdinalIgnoreCase) &&
                fileName.EndsWith(txtExtension, StringComparison.OrdinalIgnoreCase));
        }
    }
}
