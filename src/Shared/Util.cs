using System.Collections;
using System.Runtime.CompilerServices;

namespace DotNetLab;

public static class Util
{
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
    /// Use in a <see langword="using"/> block to ensure it doesn't contain any <see cref="await"/>s.
    /// </summary>
    public static R EnsureSync() => default;

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
        return string.Join(separator, source.Select(x => $"{quote}{x}{quote}"));
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

    public static async Task<ImmutableArray<TResult>> SelectAsArrayAsync<T, TResult>(this IEnumerable<T> source, Func<T, Task<TResult>> selector)
    {
        var results = ImmutableArray.CreateBuilder<TResult>(source.TryGetNonEnumeratedCount(out var count) ? count : 0);
        foreach (var item in source)
        {
            results.Add(await selector(item));
        }
        return results.DrainToImmutable();
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

    public static IEnumerable<T> TryConcat<T>(this ImmutableArray<T>? a, ImmutableArray<T>? b)
    {
        return [.. (a ?? []), .. (b ?? [])];
    }

    public static T? TryAt<T>(this IReadOnlyList<T> list, int index)
    {
        if (index < 0 || index >= list.Count)
        {
            return default;
        }

        return list[index];
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
