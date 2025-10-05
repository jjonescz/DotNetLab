namespace DotNetLab;

/// <summary>
/// Maps <see cref="StringSpan"/>s to <see cref="StringSpan"/>s.
/// </summary>
public readonly struct DocumentMapping(IReadOnlyList<(StringSpan, StringSpan)> values)
{
    public readonly IReadOnlyList<(StringSpan Source, StringSpan Target)> Values = values;

    public bool IsDefault => Values is null;

    /// <summary>
    /// Finds the smallest source span containing <paramref name="position"/> and a target it maps to.
    /// In case multiple same source spans map to different targets, the largest target is chosen.
    /// </summary>
    public bool TryFind(int position, out StringSpan source, out StringSpan target)
    {
        (StringSpan Source, StringSpan Target)? result = null;
        foreach (var candidate in Values)
        {
            if (candidate.Source.Contains(position) &&
                (result is not { } previous ||
                previous.Source.Length > candidate.Source.Length ||
                (previous.Source.Length == candidate.Source.Length && previous.Target.Length < candidate.Target.Length)))
            {
                result = candidate;
            }
        }

        if (result is { } found)
        {
            source = found.Source;
            target = found.Target;
            return true;
        }

        source = default;
        target = default;
        return false;
    }

    public void Serialize(Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        foreach (var pair in Values)
        {
            writer.Write7BitEncodedInt(pair.Source.Start);
            writer.Write7BitEncodedInt(pair.Source.Length);
            writer.Write7BitEncodedInt(pair.Target.Start);
            writer.Write7BitEncodedInt(pair.Target.Length);
        }
    }

    public string Serialize()
    {
        using var stream = new MemoryStream();
        Serialize(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    public static DocumentMapping Deserialize(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var builder = ImmutableArray.CreateBuilder<(StringSpan, StringSpan)>();
        while (stream.Position < stream.Length)
        {
            builder.Add((
                new StringSpan
                {
                    Start = reader.Read7BitEncodedInt(),
                    Length = reader.Read7BitEncodedInt(),
                },
                new StringSpan
                {
                    Start = reader.Read7BitEncodedInt(),
                    Length = reader.Read7BitEncodedInt(),
                }));
        }
        return new(builder.ToArray());
    }

    public static DocumentMapping Deserialize(string serialized)
    {
        var bytes = Convert.FromBase64String(serialized);
        using var stream = new MemoryStream(bytes);
        return Deserialize(stream);
    }
}

/// <summary>
/// Like Roslyn's <c>TextSpan</c> but without the dependency on Roslyn.
/// </summary>
public readonly struct StringSpan : IComparable<StringSpan>
{
    public required int Start { get; init; }
    public required int Length { get; init; }
    public int End => Start + Length;

    public int CompareTo(StringSpan other)
    {
        int c = Start.CompareTo(other.Start);
        if (c != 0) return c;
        return Length.CompareTo(other.Length);
    }

    public bool Contains(int position)
    {
        return position >= Start && position < End;
    }

    public override string ToString()
    {
        return $"[{Start}..{End})";
    }
}
