using System.Collections;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DotNetLab;

public static partial class Util
{
    private static Guid? tupleElementNames, dynamicLocalVariables;

    [GeneratedRegex("""\s+""")]
    public static partial Regex Whitespace { get; }

    [GeneratedRegex("^file:///out/[^/]+/(?<type>[^/]+)(?<input>(/.*)?)$")]
    internal static partial Regex OutputModelUri { get; }

    extension(AsyncEnumerable)
    {
        public static IAsyncEnumerable<T> Create<T>(T item)
        {
            return AsyncEnumerable.Repeat(item, 1);
        }
    }

    extension(Guid)
    {
        public static Guid TupleElementNames => tupleElementNames ??= new("ED9FDF71-8879-4747-8ED3-FE5EDE3CE710");
        public static Guid DynamicLocalVariables => dynamicLocalVariables ??= new("83C563C4-B4F3-47D5-B824-BA5441477EA8");
    }

    extension(GZipStream)
    {
        public static byte[] Compress(ReadOnlySpan<byte> bytes)
        {
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionMode.Compress))
            {
                gzip.Write(bytes);
                gzip.Flush();
            }
            return compressed.ToArray();
        }

        public static byte[] Compress(Stream source)
        {
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionMode.Compress))
            {
                source.CopyTo(gzip);
                gzip.Flush();
            }
            return compressed.ToArray();
        }

        public static byte[] Decompress(byte[] bytes)
        {
            using var decompressed = new MemoryStream();
            using (var compressed = new MemoryStream(bytes))
            using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
            {
                gzip.CopyTo(decompressed);
            }
            return decompressed.ToArray();
        }
    }

    extension<TKey, TValue>(IDictionary<TKey, TValue> dictionary)
    {
        public void SetRange(IEnumerable<KeyValuePair<TKey, TValue>> items)
        {
            foreach (var item in items)
            {
                dictionary[item.Key] = item.Value;
            }
        }
    }

    extension<T>(IEnumerable<T> collection)
    {
        public IList<T> AsList()
        {
            return collection is IList<T> list ? list : [.. collection];
        }

        public IReadOnlyList<T> AsReadOnlyList()
        {
            return collection is IReadOnlyList<T> list ? list : [.. collection];
        }

        public async Task<T?> FirstOrDefaultAsync(Func<T, ValueTask<bool>> predicate)
        {
            foreach (var item in collection)
            {
                if (await predicate(item))
                {
                    return item;
                }
            }

            return default;
        }
    }

    extension<T>(IList<T> list)
    {
        public void Sort(IComparer<T> comparer)
        {
            if (list is List<T> concreteList)
            {
                concreteList.Sort(comparer);
            }
            else
            {
                ArrayList.Adapter((IList)list).Sort((IComparer)comparer);
            }
        }
    }

    extension<T>(ImmutableArray<T> array)
    {
        public ImmutableArray<T>.Builder ToBuilder(int additionalCapacity)
        {
            var builder = ImmutableArray.CreateBuilder<T>(array.Length + additionalCapacity);
            builder.AddRange(array);
            return builder;
        }

        public ImmutableArray<T> WhereAsArray(Func<T, bool> predicate)
        {
            var builder = ImmutableArray.CreateBuilder<T>(array.Length);
            foreach (var item in array)
            {
                if (predicate(item))
                {
                    builder.Add(item);
                }
            }
            return builder.DrainToImmutable();
        }
    }

    extension(ReadOnlySpan<char> span)
    {
        public Regex.ValueSplitEnumerator SplitByWhitespace(int count)
        {
            return Whitespace.EnumerateSplits(span, count);
        }
    }

    extension<T>(ValueTask<T> task)
    {
        public Task<TResult> SelectAsTask<TResult>(Func<T, TResult> selector)
        {
            if (task.IsCompletedSuccessfully)
            {
                return Task.FromResult(selector(task.Result));
            }

            return selectAsync();

            async Task<TResult> selectAsync()
            {
                var result = await task;
                return selector(result);
            }
        }
    }

    public static void AddRange<T>(this ICollection<T> collection, IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    public static void AddRange<T>(this ICollection<T> collection, ReadOnlySpan<T> items)
    {
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    public static async Task<(string stdout, string stderr)> CaptureConsoleOutputAsync(Func<Task> action)
    {
        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(stdoutWriter);
        Console.SetError(stderrWriter);
        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
        var stdout = stdoutWriter.ToString();
        var stderr = stderrWriter.ToString();
        return (stdout, stderr);
    }

    public static async IAsyncEnumerable<T> Concat<T>(this IAsyncEnumerable<T> a, IEnumerable<T> b)
    {
        await foreach (var item in a)
        {
            yield return item;
        }
        foreach (var item in b)
        {
            yield return item;
        }
    }

    public static async IAsyncEnumerable<T> Concat<T>(this IAsyncEnumerable<T> a, IEnumerable<Task<T>> b)
    {
        await foreach (var item in a)
        {
            yield return item;
        }
        foreach (var item in b)
        {
            yield return await item;
        }
    }

    /// <summary>
    /// Use in a <see langword="using"/> block to ensure it doesn't contain any <see langword="await"/>s.
    /// </summary>
    public static R EnsureSync() => default;

    public static async Task<T?> FirstOrNullAsync<T>(this IAsyncEnumerable<T> source) where T : struct
    {
        await foreach (var item in source)
        {
            return item;
        }

        return null;
    }

    public static async Task<T?> FirstOrNullAsync<T>(this IAsyncEnumerable<T> source, Func<T, bool> predicate) where T : struct
    {
        await foreach (var item in source)
        {
            if (predicate(item))
            {
                return item;
            }
        }

        return null;
    }

    public static async Task<T?> FirstOrNullAsync<T>(this IAsyncEnumerable<T> source, Func<T, Task<bool>> predicate) where T : struct
    {
        await foreach (var item in source)
        {
            if (await predicate(item))
            {
                return item;
            }
        }

        return null;
    }

    public static void ForEach<T>(this IEnumerable<T> source, Action<T> action)
    {
        foreach (var item in source)
        {
            action(item);
        }
    }

    public static string GetAssemblyDiskPath(string assemblyName)
    {
        return Path.Join(AppContext.BaseDirectory, $"{assemblyName}.dll");
    }

    public static string GetFirstLine(this string text)
    {
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            return line.ToString();
        }

        return text;
    }

    public static TValue GetOrAdd<TKey, TValue>(
        this IDictionary<TKey, TValue> dictionary,
        TKey key,
        TValue value)
        where TKey : notnull
    {
        if (dictionary.TryGetValue(key, out var existingValue))
        {
            return existingValue;
        }

        dictionary.Add(key, value);
        return value;
    }

    public static bool IsCSharpFileName(this string fileName) => fileName.IsCSharpFileName(out _);

    public static bool IsCSharpFileName(this string fileName, out bool script)
    {
        return (script = fileName.EndsWith(".csx", StringComparison.OrdinalIgnoreCase)) ||
            fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRazorFileName(this string fileName)
    {
        return fileName.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
    }

    public static string JoinToString<T>(this IEnumerable<T> source, string separator)
    {
        return string.Join(separator, source);
    }

    public static string JoinToString<T>(this IEnumerable<T> source, string separator, string quote)
    {
        return source.JoinToString(separator, quote, quote);
    }

    public static string JoinToString<T>(this IEnumerable<T> source, string separator, string prefix, string suffix)
    {
        return string.Join(separator, source.Select(x => $"{prefix}{x}{suffix}"));
    }

    public static async IAsyncEnumerable<TResult> Select<T, TResult>(this IAsyncEnumerable<T> source, Func<T, Task<TResult>> selector)
    {
        await foreach (var item in source)
        {
            yield return await selector(item);
        }
    }

    public static async Task<IEnumerable<TResult>> SelectAsync<T, TResult>(this IEnumerable<T> source, Func<T, Task<TResult>> selector)
    {
        var results = new List<TResult>(source.TryGetNonEnumeratedCount(out var count) ? count : 0);
        foreach (var item in source)
        {
            results.Add(await selector(item));
        }
        return results;
    }

    public static ImmutableArray<TResult> SelectAsArray<T, TResult>(this IEnumerable<T> source, Func<T, TResult> selector)
    {
        var results = ImmutableArray.CreateBuilder<TResult>(source.TryGetNonEnumeratedCount(out var count) ? count : 0);
        foreach (var item in source)
        {
            results.Add(selector(item));
        }
        return results.DrainToImmutable();
    }

    public static async Task<ImmutableArray<TResult>> SelectAsArrayAsync<T, TResult>(this IEnumerable<T> source, Func<T, Task<TResult>> selector)
    {
        var results = ImmutableArray.CreateBuilder<TResult>(source.TryGetNonEnumeratedCount(out var count) ? count : 0);
        foreach (var item in source)
        {
            results.Add(await selector(item));
        }
        return results.DrainToImmutable();
    }

    public static async IAsyncEnumerable<TResult> SelectMany<T, TCollection, TResult>(this IAsyncEnumerable<T> source, Func<T, Task<IEnumerable<TCollection>>> selector, Func<T, TCollection, TResult> resultSelector)
    {
        await foreach (var item in source)
        {
            foreach (var subitem in await selector(item))
            {
                yield return resultSelector(item, subitem);
            }
        }
    }

    public static async Task<IEnumerable<TResult>> SelectManyAsync<T, TResult>(this IEnumerable<T> source, Func<T, Task<IEnumerable<TResult>>> selector)
    {
        var results = new List<TResult>();
        foreach (var item in source)
        {
            results.AddRange(await selector(item));
        }
        return results;
    }

    public static async Task<IEnumerable<TResult>> SelectManyAsync<T, TCollection, TResult>(this IEnumerable<T> source, Func<T, Task<IEnumerable<TCollection>>> selector, Func<T, TCollection, TResult> resultSelector)
    {
        var results = new List<TResult>();
        foreach (var item in source)
        {
            foreach (var subitem in await selector(item))
            {
                results.AddRange(resultSelector(item, subitem));
            }
        }
        return results;
    }

    public static IEnumerable<TResult> SelectNonNull<T, TResult>(this IEnumerable<T> source, Func<T, TResult?> selector)
    {
        foreach (var item in source)
        {
            if (selector(item) is TResult result)
            {
                yield return result;
            }
        }
    }

    public static async IAsyncEnumerable<TResult> SelectNonNull<T, TResult>(this IAsyncEnumerable<T> source, Func<T, Task<TResult?>> selector)
    {
        await foreach (var item in source)
        {
            if (await selector(item) is TResult result)
            {
                yield return result;
            }
        }
    }

    public static async Task<IEnumerable<TResult>> SelectNonNullAsync<T, TResult>(this IEnumerable<T> source, Func<T, Task<TResult?>> selector)
    {
        var results = new List<TResult>(source.TryGetNonEnumeratedCount(out var count) ? count : 0);
        foreach (var item in source)
        {
            if (await selector(item) is TResult result)
            {
                results.Add(result);
            }
        }
        return results;
    }

    public static string SeparateThousands<T>(this T number) where T : IFormattable
    {
        return number.ToString(format: "N0", formatProvider: null);
    }

    public static async Task<Dictionary<TKey, TValue>> ToDictionaryAsync<T, TKey, TValue>(
        this IAsyncEnumerable<T> source,
        Func<T, TKey> keySelector,
        Func<T, TValue> valueSelector)
        where TKey : notnull
    {
        var dictionary = new Dictionary<TKey, TValue>();
        await foreach (var item in source)
        {
            dictionary.Add(keySelector(item), valueSelector(item));
        }
        return dictionary;
    }

    public static async Task<ImmutableArray<T>> ToImmutableArrayAsync<T>(this IAsyncEnumerable<T> source)
    {
        var builder = ImmutableArray.CreateBuilder<T>();
        await foreach (var item in source)
        {
            builder.Add(item);
        }
        return builder.ToImmutable();
    }

    public static async Task<ImmutableDictionary<TKey, TValue>> ToImmutableDictionaryAsync<T, TKey, TValue>(
        this IEnumerable<T> source,
        Func<T, TKey> keySelector,
        Func<T, Task<TValue>> valueSelector)
        where TKey : notnull
    {
        var builder = ImmutableDictionary.CreateBuilder<TKey, TValue>();

        foreach (var item in source)
        {
            builder.Add(keySelector(item), await valueSelector(item));
        }

        return builder.ToImmutable();
    }

    public static T? TryAt<T>(this IReadOnlyList<T> list, int index)
    {
        if (index < 0 || index >= list.Count)
        {
            return default;
        }

        return list[index];
    }

    public static IEnumerable<T> TryConcat<T>(this ImmutableArray<T>? a, ImmutableArray<T>? b)
    {
        return [.. (a ?? []), .. (b ?? [])];
    }

    public static async Task<T?> TryReadFromJsonAsync<T>(this HttpContent content, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            return await content.ReadFromJsonAsync(jsonTypeInfo, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static InvalidOperationException Unexpected<T>(T value, [CallerArgumentExpression(nameof(value))] string name = "")
    {
        return new($"Unexpected {name}='{value}' of type '{value?.GetType().FullName ?? "null"}'.");
    }

    public static T Unreachable<T>()
    {
        throw new InvalidOperationException($"Unreachable '{typeof(T)}'.");
    }

    public static string WithoutSuffix(this string s, string suffix)
    {
        return s.EndsWith(suffix, StringComparison.Ordinal) ? s[..^suffix.Length] : s;
    }
}

public readonly ref struct R
{
    public void Dispose() { }
}

[UnsupportedOSPlatform("browser")]
public readonly ref struct BrotliCompressor(ReadOnlySpan<byte> bytes)
{
    private readonly ReadOnlySpan<byte> bytes = bytes;

    public int BufferLength => sizeof(int) + BrotliEncoder.GetMaxCompressedLength(bytes.Length);

    public ReadOnlySpan<byte> Compress(Span<byte> buffer)
    {
        Debug.Assert(buffer.Length == BufferLength);

        if (!BitConverter.TryWriteBytes(buffer, bytes.Length))
        {
            throw new InvalidOperationException("Failed to write length prefix for Brotli compression.");
        }

        if (!BrotliEncoder.TryCompress(bytes, buffer[sizeof(int)..], out int bytesWritten))
        {
            throw new InvalidOperationException("Brotli compression failed.");
        }

        return buffer[..(sizeof(int) + bytesWritten)];
    }
}

[UnsupportedOSPlatform("browser")]
public readonly ref struct BrotliDecompressor(ReadOnlySpan<byte> compressed)
{
    private readonly ReadOnlySpan<byte> compressed = compressed;

    public int BufferLength => BitConverter.ToInt32(compressed);

    public ReadOnlySpan<byte> Decompress(Span<byte> buffer)
    {
        Debug.Assert(buffer.Length == BufferLength);

        if (!BrotliDecoder.TryDecompress(compressed[sizeof(int)..], buffer, out int bytesWritten))
        {
            throw new InvalidOperationException("Brotli decompression failed.");
        }

        Debug.Assert(bytesWritten == BufferLength);

        return buffer[..bytesWritten];
    }
}
