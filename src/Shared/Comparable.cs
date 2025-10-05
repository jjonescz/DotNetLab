namespace DotNetLab;

public readonly struct Comparable<T, TComparer>(T value)
    : IEquatable<Comparable<T, TComparer>>
    where TComparer : IComparerFactory<T>
{
    public T Value { get; } = value;

    public bool Equals(Comparable<T, TComparer> other)
    {
        return TComparer.EqualityComparer.Equals(Value, other.Value);
    }

    public override bool Equals([NotNullWhen(returnValue: true)] object? obj)
    {
        return obj is Comparable<T, TComparer> other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Value is null ? 0 : TComparer.EqualityComparer.GetHashCode(Value);
    }

    public override string? ToString()
    {
        return Value?.ToString();
    }

    public static bool operator ==(Comparable<T, TComparer> left, Comparable<T, TComparer> right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Comparable<T, TComparer> left, Comparable<T, TComparer> right)
    {
        return !left.Equals(right);
    }

    public static implicit operator Comparable<T, TComparer>(T value)
    {
        return new(value);
    }

    public static implicit operator T(Comparable<T, TComparer> comparable)
    {
        return comparable.Value;
    }
}

public interface IComparerFactory<T>
{
    static abstract IEqualityComparer<T> EqualityComparer { get; }
}

public static class Comparers
{
    public sealed class Default<T> : IComparerFactory<T>
    {
        private Default() { }

        public static IEqualityComparer<T> EqualityComparer => EqualityComparer<T>.Default;
    }

    public sealed class ImmutableArray<T, TComparer>
        : IComparerFactory<ImmutableArray<T>>,
        IEqualityComparer<ImmutableArray<T>>
        where TComparer : IComparerFactory<T>
    {
        private ImmutableArray() { }

        public static IEqualityComparer<ImmutableArray<T>> EqualityComparer { get; } = new ImmutableArray<T, TComparer>();

        public bool Equals(ImmutableArray<T> x, ImmutableArray<T> y)
        {
            if (x.IsDefault) return y.IsDefault;
            if (y.IsDefault) return false;
            return x.SequenceEqual(y, TComparer.EqualityComparer);
        }

        public int GetHashCode(ImmutableArray<T> obj)
        {
            if (obj.IsDefault) return 0;
            var hash = new HashCode();
            var comparer = TComparer.EqualityComparer;
            foreach (var item in obj)
            {
                if (item is not null)
                {
                    hash.Add(comparer.GetHashCode(item));
                }
            }
            return hash.ToHashCode();
        }
    }

    public sealed class ImmutableDictionary<TKey, TValue, TValueComparer>
        : IComparerFactory<ImmutableDictionary<TKey, TValue>>,
        IEqualityComparer<ImmutableDictionary<TKey, TValue>>
        where TKey : notnull
        where TValueComparer : IComparerFactory<TValue>
    {
        private ImmutableDictionary() { }

        public static IEqualityComparer<ImmutableDictionary<TKey, TValue>> EqualityComparer { get; } = new ImmutableDictionary<TKey, TValue, TValueComparer>();

        public bool Equals(ImmutableDictionary<TKey, TValue>? x, ImmutableDictionary<TKey, TValue>? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            if (x.Count != y.Count) return false;
            if (x.KeyComparer != y.KeyComparer) return false;
            var valueComparer = TValueComparer.EqualityComparer;
            foreach (var kvp in x)
            {
                if (!y.TryGetValue(kvp.Key, out var yValue)) return false;
                if (!valueComparer.Equals(kvp.Value, yValue)) return false;
            }
            return true;
        }

        public int GetHashCode(ImmutableDictionary<TKey, TValue>? obj)
        {
            if (obj is null) return 0;
            var hash = new HashCode();
            var valueComparer = TValueComparer.EqualityComparer;
            foreach (var kvp in obj)
            {
                if (kvp.Value is { } value)
                {
                    hash.Add(valueComparer.GetHashCode(value));
                }
            }
            return hash.ToHashCode();
        }
    }

    public static class String
    {
        public sealed class OrdinalIgnoreCase : IComparerFactory<string>
        {
            private OrdinalIgnoreCase() { }

            public static IEqualityComparer<string> EqualityComparer => StringComparer.OrdinalIgnoreCase;
        }
    }
}
