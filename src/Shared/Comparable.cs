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
    public static class String
    {
        public sealed class OrdinalIgnoreCase : IComparerFactory<string>
        {
            private OrdinalIgnoreCase() { }

            public static IEqualityComparer<string> EqualityComparer => StringComparer.OrdinalIgnoreCase;
        }
    }
}
