using AwesomeAssertions;
using DotNetLab.Editor;

namespace DotNetLab;

[TestClass]
public sealed class MonacoTextPatchTests
{
    [TestMethod]
    public void Apply_InsertsInTheMiddle()
    {
        MonacoTextPatch.TryApply("ac", [new(1, 0, "b")], out var next).Should().BeTrue();
        next.Should().Be("abc");
    }

    [TestMethod]
    public void Apply_ReplacesAndDeletes()
    {
        MonacoTextPatch.TryApply("hello world", [new(6, 5, "there")], out var replaced).Should().BeTrue();
        replaced.Should().Be("hello there");

        MonacoTextPatch.TryApply("hello world", [new(5, 6, "")], out var deleted).Should().BeTrue();
        deleted.Should().Be("hello");
    }

    [TestMethod]
    public void Apply_MultipleDisjointEdits_UsesOriginalOffsets()
    {
        // "0123456789" → delete "23" and replace "67" with "xx" (Monaco-style original offsets)
        var edits = new MonacoTextPatch.Edit[]
        {
            new(6, 2, "xx"),
            new(2, 2, ""),
        };

        MonacoTextPatch.TryApply("0123456789", edits, out var next).Should().BeTrue();
        next.Should().Be("0145xx89");
        TryApplyConcatLoop("0123456789", edits, out var baseline).Should().BeTrue();
        next.Should().Be(baseline);
    }

    [TestMethod]
    public void Apply_RejectsEmptyOutOfRangeAndOverlap()
    {
        MonacoTextPatch.TryApply("ab", [], out var empty).Should().BeFalse();
        empty.Should().Be("ab");

        MonacoTextPatch.TryApply("ab", [new(3, 0, "x")], out _).Should().BeFalse();
        MonacoTextPatch.TryApply("ab", [new(0, 3, "x")], out _).Should().BeFalse();
        MonacoTextPatch.TryApply("abcd", [new(0, 2, "x"), new(1, 1, "y")], out _).Should().BeFalse();
    }

    [TestMethod]
    public void Apply_MatchesConcatLoop_OnTemplateAndScatteredEdits()
    {
        var template = Lab.InitialCode.CSharp.TextTemplate;
        var one = new MonacoTextPatch.Edit[] { new(template.Length / 2, 0, "X") };
        MonacoTextPatch.TryApply(template, one, out var patched).Should().BeTrue();
        TryApplyConcatLoop(template, one, out var concat).Should().BeTrue();
        patched.Should().Be(concat);

        var source = new string('a', 8_192);
        var many = new MonacoTextPatch.Edit[]
        {
            new(100, 1, "BB"),
            new(4_000, 2, ""),
            new(7_000, 0, "ccc"),
        };
        MonacoTextPatch.TryApply(source, many, out var manyPatched).Should().BeTrue();
        TryApplyConcatLoop(source, many, out var manyConcat).Should().BeTrue();
        manyPatched.Should().Be(manyConcat);
    }

    /// <summary>
    /// Old LabCodeEditor loop: full Concat copy per edit, applied from the end.
    /// </summary>
    internal static bool TryApplyConcatLoop(string source, IReadOnlyList<MonacoTextPatch.Edit> edits, out string next)
    {
        next = source;
        if (edits is not { Count: > 0 })
        {
            return false;
        }

        foreach (var edit in edits.OrderByDescending(static e => e.Offset))
        {
            var offset = edit.Offset;
            var length = edit.Length;
            if (offset < 0 || length < 0 || offset + length > next.Length)
            {
                next = source;
                return false;
            }

            next = string.Concat(next.AsSpan(0, offset), edit.Text ?? "", next.AsSpan(offset + length));
        }

        return true;
    }
}
