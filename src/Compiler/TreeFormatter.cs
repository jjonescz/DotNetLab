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

        var stack = new Stack<Work>();
        var writer = new Writer();

        stack.Push(new() { Obj = obj, Depth = 0, Parents = [] });

        var children = new List<Work>();

        while (stack.TryPop(out var work))
        {
            Debug.Assert(work.Depth >= 0 && work.Depth <= maxDepth);

            children.Clear();

            writer.SetDepth(work.Depth);

            if (work.Property is { } property)
            {
                writer.Write(".", ClassificationTypeNames.Punctuation);
                writer.Write(property.Name, ClassificationTypeNames.PropertyName);
                writer.Write(" = ", ClassificationTypeNames.Punctuation);
            }

            if (work.AddParent(out var newParents) == false)
            {
                writer.Write("..", ClassificationTypeNames.Punctuation);
                writer.Write("recursive", ClassificationTypeNames.Keyword);
                writer.WriteLine();
                continue;
            }

            if (SymbolDisplay.FormatPrimitive(work.Obj!, quoteStrings: true, useHexadecimalNumbers: false) is { } formatted)
            {
                writer.WriteLine(formatted, ClassificationTypeNames.Keyword);
                continue;
            }

            Debug.Assert(work.Obj != null);

            if (work.Obj is TextSpan textSpan)
            {
                writer.WriteLine(textSpan.ToString(), ClassificationTypeNames.StringLiteral);
                continue;
            }

            var type = work.Obj.GetType();
            writer.WriteLine(type.Name, type.IsValueType ? ClassificationTypeNames.StructName : ClassificationTypeNames.ClassName);

            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static p => p.GetIndexParameters().Length == 0).ToList();

            if (properties.Count != 0)
            {
                if (!tryNest(out var nested))
                {
                    continue;
                }

                foreach (var p in properties)
                {
                    object? o; try { o = p.GetValue(work.Obj); } catch (Exception ex) { o = ex; }
                    children.Add(new() { Property = p, Obj = o, Depth = nested, Parents = newParents });
                }
            }

            if (work.Obj is IEnumerable enumerable)
            {
                if (!tryNest(out var nested))
                {
                    continue;
                }

                try
                {
                    foreach (var item in enumerable)
                    {
                        children.Add(new() { Obj = item, Depth = nested, Parents = newParents });
                    }
                }
                catch (Exception ex)
                {
                    children.Add(new() { Obj = ex, Depth = nested, Parents = newParents });
                }
            }

            for (int i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }

            bool tryNest(out int nested)
            {
                nested = work.Depth + 1;

                if (nested > maxDepth)
                {
                    using var _ = writer.Indent();
                    writer.Write("..", ClassificationTypeNames.Punctuation);
                    writer.Write("error", ClassificationTypeNames.Keyword);
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

    private readonly struct Work
    {
        public PropertyInfo? Property { get; init; }
        public required object? Obj { get; init; }
        public required int Depth { get; init; }
        public required ImmutableHashSet<object> Parents { get; init; }

        public bool? AddParent(out ImmutableHashSet<object> newParents)
        {
            if (Obj == null || Obj.GetType().IsValueType)
            {
                newParents = Parents;
                return null;
            }

            newParents = Parents.Add(Obj);

            if (newParents.Count != Parents.Count)
            {
                Debug.Assert(newParents.Count == Parents.Count + 1);
                return true;
            }

            return false;
        }
    }

    public readonly struct Result
    {
        public required string Text { get; init; }
        public required ImmutableArray<ClassifiedSpan> ClassifiedSpans { get; init; }
    }
}
