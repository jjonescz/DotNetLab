using System.Collections;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DotNetLab;

public static partial class Util
{
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
        public ValueTask<TResult> Select<TResult>(Func<T, TResult> selector)
        {
            if (task.IsCompletedSuccessfully)
            {
                return new(selector(task.Result));
            }

            return new(task.AsTask()
                .ContinueWith(t => selector(t.Result), TaskContinuationOptions.OnlyOnRanToCompletion));
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

    public static void CaptureConsoleOutput(Action action, out string stdout, out string stderr)
    {
        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(stdoutWriter);
        Console.SetError(stderrWriter);
        try
        {
            action();
        }
        finally
        {
            stdout = stdoutWriter.ToString();
            stderr = stderrWriter.ToString();
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
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
