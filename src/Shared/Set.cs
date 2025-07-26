namespace DotNetLab;

/// <summary>
/// An equatable <see cref="IReadOnlySet{T}"/>.
/// </summary>
public readonly struct Set<T>(IReadOnlySet<T> value) : IEquatable<Set<T>>
{
    public IReadOnlySet<T> Value { get; } = value;

    public bool Equals(Set<T> other)
    {
        if (Value is null) return other.Value is null;
        if (other.Value is null) return false;
        return Value.SetEquals(other.Value);
    }

    public override bool Equals(object? obj)
    {
        return obj is Set<T> set && Equals(set);
    }

    public static bool operator ==(Set<T> left, Set<T> right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Set<T> left, Set<T> right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        if (Value is null) return 0;

        int hash = 0;
        var comparer = EqualityComparer<T>.Default;
        foreach (var item in Value)
        {
            if (item is not null)
            {
                hash ^= comparer.GetHashCode(item);
            }
        }

        return hash;
    }

    public override string ToString()
    {
        return Value is null ? string.Empty : Value.JoinToString(", ");
    }

    public static implicit operator Set<T>(HashSet<T> value)
    {
        return new(value);
    }
}
