using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Collections;

namespace DotNetLab;

public sealed class TreeFormatter
{
    public Result Format(object? obj)
    {
        const int maxDepth = 10;

        var writer = new Writer();

        format(obj: obj, depth: 0, parents: []);

        void format(object? obj, int depth, ImmutableHashSet<object> parents, PropertyInfo? property = null)
        {
            Debug.Assert(depth >= 0 && depth <= maxDepth);

            writer.SetDepth(depth);

            if (property != null)
            {
                writer.Write(".", ClassificationTypeNames.Punctuation);
                writer.Write(property.Name, ClassificationTypeNames.PropertyName);
                writer.Write(" = ", ClassificationTypeNames.Punctuation);
            }

            if (addParent(out var newParents) == false)
            {
                writer.Write("..recursive", ClassificationTypeNames.Keyword);
                writer.WriteLine();
                return;
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

            Debug.Assert(obj != null);

            if (obj is TextSpan textSpan)
            {
                writer.WriteLine(textSpan.ToString(), ClassificationTypeNames.NumericLiteral);
                return;
            }

            var type = obj.GetType();
            writer.WriteLine(type.Name, type.IsValueType ? ClassificationTypeNames.StructName : ClassificationTypeNames.ClassName);

            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static p => p.GetIndexParameters().Length == 0).ToList();

            if (properties.Count != 0)
            {
                if (!tryNest(out var nested))
                {
                    return;
                }

                foreach (var p in properties)
                {
                    object? o; try { o = p.GetValue(obj); } catch (Exception ex) { o = ex; }
                    format(o, nested, newParents, p);
                }
            }

            if (obj is IEnumerable enumerable)
            {
                if (!tryNest(out var nested))
                {
                    return;
                }

                try
                {
                    foreach (var item in enumerable)
                    {
                        format(item, nested, newParents);
                    }
                }
                catch (Exception ex)
                {
                    format(ex, nested, newParents);
                }
            }

            bool? addParent(out ImmutableHashSet<object> newParents)
            {
                if (obj == null || obj.GetType().IsValueType)
                {
                    newParents = parents;
                    return null;
                }

                newParents = parents.Add(obj);

                if (newParents.Count != parents.Count)
                {
                    Debug.Assert(newParents.Count == parents.Count + 1);
                    return true;
                }

                return false;
            }

            bool tryNest(out int nested)
            {
                nested = depth + 1;

                if (nested > maxDepth)
                {
                    using var _ = writer.Indent();
                    writer.Write("..error", ClassificationTypeNames.Keyword);
                    writer.Write(" = ", ClassificationTypeNames.Punctuation);
                    writer.Write($"maximum depth ({maxDepth}) reached", ClassificationTypeNames.StringLiteral);
                    writer.WriteLine();
                    return false;
                }

                return true;
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
        private const int indentSize = 2;

        private readonly StringBuilder sb = new();
        private readonly ImmutableArray<ClassifiedSpan>.Builder classifiedSpans = ImmutableArray.CreateBuilder<ClassifiedSpan>();
        private int? needsIndent;
        private int depth;

        [UnscopedRef]
        public Scope Indent()
        {
            return new Scope(ref this);
        }

        public void SetDepth(int value)
        {
            Debug.Assert(value >= 0);
            Debug.Assert(needsIndent is null || needsIndent == depth);

            depth = value;
            if (needsIndent != null)
            {
                needsIndent = value;
            }
        }

        public void Write(string value, string classification)
        {
            if (needsIndent is { } indent)
            {
                sb.Append(value: ' ', repeatCount: indent * indentSize);
                needsIndent = null;
            }

            sb.Append(value);
            classifiedSpans.Add(new ClassifiedSpan(
                new TextSpan(sb.Length - value.Length, value.Length),
                classification));
        }

        public void WriteLine(string value, string classification)
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
