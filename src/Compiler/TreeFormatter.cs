using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Collections;

namespace DotNetLab;

public sealed class TreeFormatter
{
    public Result Format(object? obj)
    {
        var writer = new Writer();

        format(obj: obj, parents: []);

        void format(object? obj, ImmutableHashSet<object> parents)
        {
            if (addParent() == false)
            {
                writer.Write("..recursive", ClassificationTypeNames.Keyword);
                writer.WriteLine();
                return;
            }

            var type = obj?.GetType();

            if (type != null)
            {
                Debug.Assert(obj != null);

                if (type.IsEnum)
                {
                    writer.WriteLine(obj.ToString(), ClassificationTypeNames.EnumMemberName);
                    return;
                }
            }

            if (SymbolDisplay.FormatPrimitive(obj!, quoteStrings: true, useHexadecimalNumbers: false) is { } formatted)
            {
                writer.WriteLine(formatted,
                    formatted.StartsWith('"')
                    ? ClassificationTypeNames.StringLiteral
                    : formatted is [{ } c, ..] && char.IsAsciiDigit(c)
                    ? ClassificationTypeNames.NumericLiteral
                    : ClassificationTypeNames.Keyword);
                return;
            }

            Debug.Assert(type != null);

            if (obj is TextSpan textSpan)
            {
                writer.WriteLine(textSpan.ToString(), ClassificationTypeNames.NumericLiteral);
                return;
            }

            writer.WriteLine(type.Name, type.IsValueType ? ClassificationTypeNames.StructName : ClassificationTypeNames.ClassName);

            using var _ = writer.TryNest(out bool success);
            if (!success)
            {
                return;
            }

            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static p => p.GetIndexParameters().Length == 0).ToList();

            if (properties.Count != 0)
            {
                foreach (var property in properties)
                {
                    writer.Write(".", ClassificationTypeNames.Punctuation);
                    writer.Write(property.Name, ClassificationTypeNames.PropertyName);
                    writer.Write(" = ", ClassificationTypeNames.Punctuation);

                    if (property.PropertyType.IsByRefLike)
                    {
                        // Cannot obtain ref structs via reflection, so just write the type.
                        writer.Write("ref struct ", ClassificationTypeNames.Keyword);
                        writer.WriteLine(property.PropertyType.Name, ClassificationTypeNames.StructName);
                        continue;
                    }

                    object? o; try { o = property.GetValue(obj); } catch (Exception ex) { o = ex; }
                    format(o, parents);
                }
            }

            if (obj is IEnumerable enumerable)
            {
                try
                {
                    foreach (var item in enumerable)
                    {
                        format(item, parents);
                    }
                }
                catch (Exception ex)
                {
                    format(ex, parents);
                }
            }

            bool? addParent()
            {
                if (obj == null || obj.GetType().IsValueType)
                {
                    return null;
                }

                var newParents = parents.Add(obj);

                if (newParents.Count != parents.Count)
                {
                    Debug.Assert(newParents.Count == parents.Count + 1);
                    parents = newParents;
                    return true;
                }

                Debug.Assert(parents == newParents);
                return false;
            }
        }

        return new()
        {
            Text = writer.ToString(),
            ClassifiedSpans = writer.GetClassifiedSpans(),
        };
    }

    [NonCopyable]
    private struct Writer()
    {
        private const int maxDepth = 10;
        private const int indentSize = 2;

        private readonly StringBuilder sb = new();
        private readonly ImmutableArray<ClassifiedSpan>.Builder classifiedSpans = ImmutableArray.CreateBuilder<ClassifiedSpan>();
        private int? needsIndent;
        private int depth;

        [UnscopedRef]
        public Scope TryNest(out bool success)
        {
            var nested = depth + 1;
            if (nested > maxDepth)
            {
                var scope = new Scope(ref this);
                Write("..error", ClassificationTypeNames.Keyword);
                Write(" = ", ClassificationTypeNames.Punctuation);
                Write($"maximum depth ({maxDepth}) reached", ClassificationTypeNames.StringLiteral);
                WriteLine();
                success = false;
                return scope;
            }

            success = true;
            return new Scope(ref this);
        }

        private void SetDepth(int value)
        {
            Debug.Assert(value >= 0);
            Debug.Assert(needsIndent is null || needsIndent == depth);

            depth = value;
            if (needsIndent != null)
            {
                needsIndent = value;
            }
        }

        public void Write(string? value, string classification)
        {
            if (needsIndent is { } indent)
            {
                sb.Append(value: ' ', repeatCount: indent * indentSize);
                needsIndent = null;
            }

            sb.Append(value);

            if (value != null)
            {
                classifiedSpans.Add(new ClassifiedSpan(
                    new TextSpan(sb.Length - value.Length, value.Length),
                    classification));
            }
        }

        public void WriteLine(string? value, string classification)
        {
            Write(value, classification);
            WriteLine();
        }

        public void WriteLine()
        {
            sb.AppendLine();
            needsIndent = depth;
        }

        public override readonly string ToString()
        {
            return sb.ToString();
        }

        public readonly ImmutableArray<ClassifiedSpan> GetClassifiedSpans()
        {
            return classifiedSpans.ToImmutable();
        }


        [NonCopyable]
        public ref struct Scope
        {
            private ref Writer writer;
            private readonly int originalDepth;

            public Scope(ref Writer writer)
            {
                this.writer = ref writer;
                originalDepth = writer.depth;
                writer.SetDepth(originalDepth + 1);
            }

            public void Dispose()
            {
                Debug.Assert(writer.depth == originalDepth + 1);
                writer.SetDepth(originalDepth);
            }
        }
    }

    public readonly struct Result
    {
        public required string Text { get; init; }
        public required ImmutableArray<ClassifiedSpan> ClassifiedSpans { get; init; }
    }
}
