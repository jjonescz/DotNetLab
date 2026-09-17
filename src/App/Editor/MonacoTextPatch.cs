namespace DotNetLab.Editor;

/// <summary>
/// Applies disjoint Monaco content changes to a C# source snapshot.
/// Offsets are against the pre-edit text (Monaco's contract).
/// </summary>
internal static class MonacoTextPatch
{
    internal readonly record struct Edit(int Offset, int Length, string? Text);

    public static bool TryApply(string source, IReadOnlyList<Edit> edits, out string next)
    {
        next = source;
        if (edits is not { Count: > 0 })
        {
            return false;
        }

        if (edits.Count == 1)
        {
            return TryApply(source, edits[0].Offset, edits[0].Length, edits[0].Text, out next);
        }

        var ordered = new Edit[edits.Count];
        for (var i = 0; i < edits.Count; i++)
        {
            ordered[i] = edits[i];
        }

        Array.Sort(ordered, static (a, b) => a.Offset.CompareTo(b.Offset));

        var newLength = source.Length;
        var prevEnd = 0;
        for (var i = 0; i < ordered.Length; i++)
        {
            var edit = ordered[i];
            if (!TryMeasure(source.Length, edit, prevEnd, ref newLength, out prevEnd))
            {
                next = source;
                return false;
            }
        }

        if (newLength < 0)
        {
            next = source;
            return false;
        }

        next = string.Create(newLength, (source, ordered), static (span, state) =>
        {
            var src = state.source.AsSpan();
            var write = 0;
            var read = 0;
            foreach (var edit in state.ordered)
            {
                var keep = edit.Offset - read;
                src.Slice(read, keep).CopyTo(span[write..]);
                write += keep;
                if (edit.Text is { Length: > 0 } text)
                {
                    text.AsSpan().CopyTo(span[write..]);
                    write += text.Length;
                }

                read = edit.Offset + edit.Length;
            }

            src[read..].CopyTo(span[write..]);
        });

        return true;
    }

    public static bool TryApply(string source, int offset, int length, string? text, out string next)
        => TryApplyOne(source, new Edit(offset, length, text), out next);

    private static bool TryApplyOne(string source, Edit edit, out string next)
    {
        next = source;
        var newLength = source.Length;
        if (!TryMeasure(source.Length, edit, prevEnd: 0, ref newLength, out _))
        {
            return false;
        }

        var text = edit.Text ?? "";
        next = string.Concat(source.AsSpan(0, edit.Offset), text, source.AsSpan(edit.Offset + edit.Length));
        return true;
    }

    private static bool TryMeasure(int sourceLength, Edit edit, int prevEnd, ref int newLength, out int end)
    {
        end = prevEnd;
        if (edit.Offset < prevEnd || edit.Length < 0 || edit.Offset > sourceLength)
        {
            return false;
        }

        end = edit.Offset + edit.Length;
        if (end < edit.Offset || end > sourceLength)
        {
            return false;
        }

        var textLength = edit.Text?.Length ?? 0;
        try
        {
            newLength = checked(newLength + textLength - edit.Length);
        }
        catch (OverflowException)
        {
            return false;
        }

        return true;
    }
}
