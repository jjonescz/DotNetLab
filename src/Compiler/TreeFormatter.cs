using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotNetLab;

public sealed class TreeFormatter
{
    public Result Format(SemanticModel model, object? obj)
    {
        var writer = new Writer();
        var seenObjects = new Dictionary<object, TextSpan>(ObjectEqualityComparer.Instance);

        format(obj: obj, parents: ImmutableHashSet.Create<object>(ReferenceEqualityComparer.Instance, []));

        return writer.Build();

        void format(object? obj, ImmutableHashSet<object> parents, bool shouldMap = true)
        {
            if (addParent() == false)
            {
                writer.Write("..recursive", ClassificationTypeNames.Keyword);
                writer.WriteLine();
                return;
            }

            if (obj is SyntaxNodeOrToken nodeOrToken)
            {
                obj = nodeOrToken.AsNode() ?? (object)nodeOrToken.AsToken();
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

            if (formatPrimitive(obj) is { } formatted)
            {
                writer.WriteLine(formatted,
                    formatted.StartsWith('"')
                    ? ClassificationTypeNames.StringLiteral
                    : formatted is [{ } c, ..] && char.IsAsciiDigit(c)
                    ? ClassificationTypeNames.NumericLiteral
                    : ClassificationTypeNames.Keyword);
                return;
            }

            Debug.Assert(type != null && obj != null);

            if (type.IsValueType && obj.Equals(RuntimeHelpers.GetUninitializedObject(type)))
            {
                writer.WriteLine("default", ClassificationTypeNames.Keyword);
                return;
            }

            if (obj is TextSpan textSpan)
            {
                using var localMap = writer.StartMap(shouldMap ? textSpan : default);
                writer.WriteLine(textSpan.ToString(), ClassificationTypeNames.NumericLiteral);
                return;
            }

            using var map = writer.StartMap(shouldMap ? getSpan(obj, type) : default);

            string? kindText = null;

            var headline = writer.StartMeasurement();

            if (isKnownCollection(obj, type, out int length, out var propertyFilter))
            {
                writer.Write("[", ClassificationTypeNames.Punctuation);
                writer.Write(length, ClassificationTypeNames.NumericLiteral);
                writer.Write("]", ClassificationTypeNames.Punctuation);
                writer.WriteLine();
            }
            else
            {
                writer.Write(type.Name, type.IsValueType ? ClassificationTypeNames.StructName : ClassificationTypeNames.ClassName);

                // Display kind as part of the type.
                if (type.IsValueType && (kindText = getKindText(obj, type)) != null)
                {
                    writer.Write("(", ClassificationTypeNames.Punctuation);
                    writer.Write(kindText, ClassificationTypeNames.EnumMemberName);
                    writer.Write(")", ClassificationTypeNames.Punctuation);
                }

                writer.WriteLine();
            }

            var headlineSpan = headline.GetSpan();

            using var scope = writer.TryNest();
            if (!scope.Success)
            {
                return;
            }

            if (seenObjects.TryGetValue(obj, out var existingSpan))
            {
                writer.GoToDefinition(headlineSpan, existingSpan);
                return;
            }

            seenObjects.Add(obj, headlineSpan);

            List<PropertyLike> properties =
            [
                // .GetOperation()
                .. PropertyLike.CreateGetOperation(model, obj),

                // Public instance properties
                .. type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    // Explicitly implemented properties of public interfaces
                    .Concat(type.GetInterfaces()
                        .Where(t => t.IsPublic)
                        .SelectMany(i => i.GetProperties()))
                    .DistinctBy(p => p.Name, StringComparer.Ordinal)
                    .Where(p => propertyFilter(p.Name) && p.GetIndexParameters().Length == 0)
                    .Select(PropertyLike.Create),

                // .GetStructure()
                .. type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(m => propertyFilter(m.Name) &&
                        m.Name is nameof(SyntaxTrivia.GetStructure) &&
                        m.ReturnType != typeof(void) && m.GetParameters() is [])
                    .Select(PropertyLike.Create),
            ];

            var isCollection = asCollection(obj, type, out var enumerable, out var compactCollection);

            if (properties.Count != 0)
            {
                // Hide less interesting properties in a subgroup.
                if (isCollection)
                {
                    displaySubgroup(properties);
                    properties = [];
                }
                else if (hasMoreInterestingProperties(obj))
                {
                    var subgroup = new List<PropertyLike>();

                    for (int i = properties.Count - 1; i >= 0; i--)
                    {
                        var property = properties[i];

                        if (!isMoreInterestingProperty(type, property))
                        {
                            subgroup.Add(property);
                            properties.RemoveAt(i);
                        }
                    }

                    displaySubgroup(subgroup);
                }

                displayProperties(properties);
            }

            if (enumerable != null)
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
                    format(Util.UnwrapException(ex), parents);
                }
            }

            if (compactCollection != null)
            {
                writer.WriteLine(compactCollection, ClassificationTypeNames.NumericLiteral);
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

            void displaySubgroup(IReadOnlyCollection<PropertyLike> properties)
            {
                if (properties.Count != 0)
                {
                    writer.WriteLine("//", ClassificationTypeNames.Comment);
                    using var scope = writer.TryNest();
                    if (scope.Success)
                    {
                        displayProperties(properties);
                    }
                }
            }

            void displayProperties(IEnumerable<PropertyLike> properties)
            {
                var isException = obj is Exception;
                var isSyntaxTrivia = !isException && obj is SyntaxTrivia;

                foreach (var property in properties)
                {
                    // Skip some uninteresting properties.
                    if (
                        // Only display message of exceptions.
                        (isException && property.Name is not nameof(Exception.Message)) ||
                        // SyntaxTree and SemanticModel is too verbose and repeated.
                        property.Type.IsAssignableTo(typeof(SyntaxTree)) ||
                        property.Type.IsAssignableTo(typeof(SemanticModel)) ||
                        // Name lists are too verbose.
                        (property.Type == typeof(ICollection<string>) && property.Name is nameof(IAssemblySymbol.TypeNames) or nameof(IAssemblySymbol.NamespaceNames)) ||
                        (property.Type == typeof(IEnumerable<string>) && property.Name is nameof(INamedTypeSymbol.MemberNames)) ||
                        // The following basically contain the parent recursively or duplicate children displayed elsewhere.
                        (isSyntaxTrivia && property.Name is nameof(SyntaxTrivia.Token)) ||
                        (property.Name is nameof(SyntaxNode.Parent) or nameof(SyntaxNode.ParentTrivia)) ||
                        (property.Name is nameof(IOperation.Syntax) or nameof(IOperation.ChildOperations) or "Children" &&
                            type.IsAssignableTo(typeof(IOperation))))
                    {
                        continue;
                    }

                    var value = property.Getter(obj);

                    using var map = writer.StartMap(value is null ? default : getSpan(value, value.GetType()));

                    writer.Write(".", ClassificationTypeNames.Punctuation);

                    if (property.IsMethod)
                    {
                        writer.Write(property.Name, ClassificationTypeNames.MethodName);
                        writer.Write("()", ClassificationTypeNames.Punctuation);
                    }
                    else
                    {
                        writer.Write(property.Name, ClassificationTypeNames.PropertyName);
                    }

                    writer.Write(" = ", ClassificationTypeNames.Punctuation);

                    if (property.Type.IsByRefLike)
                    {
                        // Cannot obtain ref structs via reflection, so just write the type.
                        writer.Write("ref struct ", ClassificationTypeNames.Keyword);
                        writer.WriteLine(property.Type.Name, ClassificationTypeNames.StructName);
                        continue;
                    }

                    // Display RawKind as <number> "<kind text>".
                    if (property.Name == nameof(SyntaxNode.RawKind) &&
                        property.Type == typeof(int) &&
                        (kindText ??= getKindText(obj, type)) != null)
                    {
                        writer.Write(formatPrimitive(value), ClassificationTypeNames.NumericLiteral);
                        writer.Write(" ", ClassificationTypeNames.WhiteSpace);
                        writer.Write(formatPrimitive(kindText), ClassificationTypeNames.StringLiteral);
                        writer.WriteLine();
                        continue;
                    }

                    format(value, parents, shouldMap: false /* we are already mapping the whole property */);
                }
            }
        }

        static TextSpan getSpan(object obj, Type type)
        {
            if (obj is TextSpan span)
            {
                return span;
            }

            if (obj is SyntaxNode node)
            {
                return node.Span;
            }

            if (obj is SyntaxToken token)
            {
                return token.Span;
            }

            if (obj is SyntaxTriviaList triviaList)
            {
                return triviaList.Span;
            }

            if (type.IsGenericType && type.IsValueType && type.GetGenericTypeDefinition() == typeof(SyntaxList<>))
            {
                return (TextSpan)type.GetProperty(nameof(SyntaxList<>.Span))!.GetValue(obj)!;
            }

            return default;
        }

        static bool isKnownCollection(object obj, Type type, out int length, out Func<string, bool> propertyFilter)
        {
            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();

                if (definition == typeof(ImmutableArray<>))
                {
                    // Note: `default` was handled previously, so this shouldn't crash.
                    length = (int)type.GetProperty(nameof(ImmutableArray<>.Length))!.GetValue(obj)!;
                    propertyFilter = static _ => false;
                    return true;
                }

                if (definition == typeof(SyntaxList<>))
                {
                    length = (int)type.GetProperty(nameof(SyntaxList<>.Count))!.GetValue(obj)!;
                    propertyFilter = static name => name != nameof(SyntaxList<>.Count);
                    return true;
                }
            }
            else // non-generic type
            {
                if (obj is SyntaxTriviaList triviaList)
                {
                    length = triviaList.Count;
                    propertyFilter = length == 0
                        ? static _ => false
                        : static name => name != nameof(SyntaxTriviaList.Count);
                    return true;
                }
            }

            length = 0;
            propertyFilter = static _ => true;
            return false;
        }

        static bool asCollection(object obj, Type type, out IEnumerable? enumerable, out string? compact)
        {
            if (type.IsGenericType && type.IsValueType)
            {
                var definition = type.GetGenericTypeDefinition();
                if (definition == typeof(SeparatedSyntaxList<>))
                {
                    enumerable = (IEnumerable)type.GetMethod(nameof(SeparatedSyntaxList<>.GetWithSeparators))!
                        .Invoke(obj, [])!;
                    compact = null;
                    return true;
                }

                if (obj is ImmutableArray<byte> { IsDefaultOrEmpty: false } byteImmutableArray)
                {
                    enumerable = null;
                    compact = "0x" + Convert.ToHexStringLower(ImmutableCollectionsMarshal.AsArray(byteImmutableArray)!);
                    return true;
                }
            }

            enumerable = obj as IEnumerable;
            compact = null;
            return enumerable != null;
        }

        static string? formatPrimitive(object? value)
        {
            return SymbolDisplay.FormatPrimitive(value!, quoteStrings: true, useHexadecimalNumbers: false);
        }

        static string? getKindText(object obj, Type type)
        {
            if (getKindTextCore(obj, type) is { } kindText)
            {
                return kindText;
            }

            if ((type == typeof(SyntaxToken)
                ? getProperty(type, "Node")
                : type == typeof(SyntaxTrivia)
                ? getProperty(type, "UnderlyingNode")
                : null) is { } nodeProperty &&
                Util.GetPropertyValueOrException(obj, nodeProperty) is { } node)
            {
                return getKindTextCore(node, node.GetType());
            }

            return null;

            static string? getKindTextCore(object obj, Type type)
            {
                if (getProperty(type, "KindText") is { } kindTextProperty &&
                    kindTextProperty.PropertyType == typeof(string) &&
                    Util.GetPropertyValueOrException(obj, kindTextProperty) is string { Length: > 0 } kindText)
                {
                    return kindText;
                }

                return null;
            }
        }

        static PropertyInfo? getProperty(Type type, string name)
        {
            return type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }

        static bool hasMoreInterestingProperties(object obj)
        {
            return obj is SyntaxNode or SyntaxToken or SyntaxTrivia or IOperation;
        }

        static bool isMoreInterestingProperty(Type parentType, in PropertyLike property)
        {
            if (property.Name is nameof(SyntaxToken.Text))
            {
                return true;
            }

            var type = property.Type;

            if (type.IsValueType)
            {
                if (type.IsGenericType)
                {
                    var definition = type.GetGenericTypeDefinition();

                    return definition == typeof(SyntaxList<>) ||
                        definition == typeof(SeparatedSyntaxList<>) ||
                        (definition == typeof(ImmutableArray<>) &&
                            type.GetGenericArguments() is [{ } arg] &&
                            arg.IsAssignableTo(typeof(IOperation)));
                }

                return type == typeof(SyntaxTrivia) ||
                    type == typeof(SyntaxTriviaList) ||
                    type == typeof(SyntaxToken);
            }

            return type.IsAssignableTo(typeof(IOperation)) ||
                type.IsAssignableTo(typeof(SyntaxNode)) ||
                type.IsAssignableTo(typeof(ISymbol));
        }
    }

    [NonCopyable]
    private struct Writer()
    {
        private const int maxDepth = 100;
        private const int maxLength = 100_000_000;
        private const int indentSize = 2;

        private readonly StringBuilder sb = new();
        private readonly ImmutableArray<ClassifiedSpan>.Builder classifiedSpans = ImmutableArray.CreateBuilder<ClassifiedSpan>();
        private readonly List<(StringSpan, StringSpan)> sourceToTree = [];
        private readonly List<(StringSpan, StringSpan)> treeToSource = [];
        private readonly List<(StringSpan, StringSpan)> treeToTree = [];
        private int? needsIndent;
        private int depth;

        private readonly int Position => sb.Length;

        [UnscopedRef]
        public Scope TryNest()
        {
            var nested = depth + 1;
            if (nested > maxDepth)
            {
                var scope = new Scope(ref this) { Success = false };
                Write("..error", ClassificationTypeNames.Keyword);
                Write(" = ", ClassificationTypeNames.Punctuation);
                Write($"maximum depth ({maxDepth}) reached", ClassificationTypeNames.StringLiteral);
                WriteLine();
                return scope;
            }

            if (Position > maxLength)
            {
                var scope = new Scope(ref this) { Success = false };
                Write("..error", ClassificationTypeNames.Keyword);
                Write(" = ", ClassificationTypeNames.Punctuation);
                Write($"maximum length ({maxLength:N0}) reached", ClassificationTypeNames.StringLiteral);
                WriteLine();
                return scope;
            }

            return new Scope(ref this) { Success = true };
        }

        [UnscopedRef]
        public SpanScope StartMap(TextSpan sourceSpan)
        {
            return new SpanScope(ref this, sourceSpan);
        }

        [UnscopedRef]
        public Measurement StartMeasurement()
        {
            return new Measurement(ref this);
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
            Write(value, classification, static (sb, value) => sb.Append(value));
        }

        public void Write(int value, string classification)
        {
            Write(value, classification, static (sb, value) => sb.Append(value));
        }

        private void Write<T>(T value, string classification, Action<StringBuilder, T> writer)
        {
            if (needsIndent is { } indent)
            {
                sb.Append(value: ' ', repeatCount: indent * indentSize);
                needsIndent = null;
            }

            int start = sb.Length;

            writer(sb, value);

            int end = sb.Length;
            int length = end - start;
            Debug.Assert(length >= 0);

            if (length > 0)
            {
                classifiedSpans.Add(new ClassifiedSpan(
                    new TextSpan(start, length),
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

        private readonly void Map(TextSpan source, TextSpan tree)
        {
            sourceToTree.Add((source.ToStringSpan(), tree.ToStringSpan()));
            treeToSource.Add((tree.ToStringSpan(), source.ToStringSpan()));
        }

        public readonly void GoToDefinition(TextSpan link, TextSpan definition)
        {
            treeToTree.Add((link.ToStringSpan(), definition.ToStringSpan()));
        }

        public Result Build()
        {
            var text = sb.ToString();
            var sourceText = SourceText.From(text);
            var semanticTokenBytes = MonacoConversions.ConvertToLspFormat(sourceText, classifiedSpans);
            var semanticTokens = Convert.ToBase64String(semanticTokenBytes);
            return new()
            {
                Text = text,
                SemanticTokens = semanticTokens,
                SourceToTree = new DocumentMapping(sourceToTree).Serialize(),
                TreeToSource = new DocumentMapping(treeToSource).Serialize(),
                TreeToTree = new DocumentMapping(treeToTree).Serialize(),
            };
        }

        [NonCopyable]
        public ref struct Scope
        {
            private ref Writer writer;
            private readonly int originalDepth;

            public required bool Success { get; init; }

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

        [NonCopyable]
        public readonly ref struct SpanScope
        {
            private readonly ref Writer writer;
            private readonly TextSpan sourceSpan;
            private readonly int treeStartPosition;

            public SpanScope(ref Writer writer, TextSpan sourceSpan)
            {
                this.writer = ref writer;
                this.sourceSpan = sourceSpan;
                treeStartPosition = writer.Position;
            }

            public void Dispose()
            {
                if (sourceSpan.IsEmpty)
                {
                    return;
                }

                var treeSpan = TextSpan.FromBounds(treeStartPosition, writer.Position);
                if (!treeSpan.IsEmpty)
                {
                    writer.Map(sourceSpan, treeSpan);
                }
            }
        }

        [NonCopyable]
        public readonly ref struct Measurement
        {
            private readonly ref Writer writer;
            private readonly int treeStartPosition;

            public Measurement(ref Writer writer)
            {
                this.writer = ref writer;
                treeStartPosition = writer.Position;
            }

            public TextSpan GetSpan()
            {
                return TextSpan.FromBounds(treeStartPosition, writer.Position);
            }
        }
    }

    private readonly struct PropertyLike
    {
        public required string Name { get; init; }
        public required Type Type { get; init; }
        public required bool IsMethod { get; init; }
        public required Func<object, object?> Getter { get; init; }

        public static PropertyLike Create(PropertyInfo property) => new()
        {
            Name = property.Name,
            Type = property.PropertyType,
            IsMethod = false,
            Getter = (obj) => Util.GetPropertyValueOrException(obj, property),
        };

        public static PropertyLike Create(MethodInfo simpleMethod) => new()
        {
            Name = simpleMethod.Name,
            Type = simpleMethod.ReturnType,
            IsMethod = true,
            Getter = (obj) =>
            {
                try
                {
                    return simpleMethod.Invoke(obj, null);
                }
                catch (Exception ex)
                {
                    return Util.UnwrapException(ex);
                }
            },
        };

        public static IEnumerable<PropertyLike> CreateGetOperation(SemanticModel model, object obj)
        {
            if (obj is SyntaxNode node)
            {
                return
                [
                    new PropertyLike
                    {
                        Name = nameof(model.GetOperation),
                        Type = typeof(IOperation),
                        IsMethod = true,
                        Getter = _ =>
                        {
                            try
                            {
                                return model.GetOperation(node);
                            }
                            catch (Exception ex)
                            {
                                return Util.UnwrapException(ex);
                            }
                        },
                    },
                ];
            }

            return [];
        }
    }

    private static class Util
    {
        public static object? GetPropertyValueOrException(object obj, PropertyInfo property)
        {
            try
            {
                return property.GetValue(obj);
            }
            catch (Exception ex)
            {
                return UnwrapException(ex);
            }
        }

        public static Exception UnwrapException(Exception ex) => ex switch
        {
            TargetInvocationException { InnerException: { } inner } => inner,
            _ => ex,
        };
    }

    private sealed class ObjectEqualityComparer : IEqualityComparer<object>
    {
        private static readonly SymbolEqualityComparer symbolComparer = SymbolEqualityComparer.Default;
        private static readonly ReferenceEqualityComparer objectComparer = ReferenceEqualityComparer.Instance;

        public static ObjectEqualityComparer Instance { get; } = new();

        private ObjectEqualityComparer() { }

        public new bool Equals(object? x, object? y)
        {
            if (x is ISymbol xSymbol && y is ISymbol ySymbol)
            {
                return symbolComparer.Equals(xSymbol, ySymbol);
            }

            return objectComparer.Equals(x, y);
        }

        public int GetHashCode(object obj)
        {
            if (obj is ISymbol symbol)
            {
                return symbolComparer.GetHashCode(symbol);
            }

            return objectComparer.GetHashCode(obj);
        }
    }

    public readonly struct Result
    {
        public required string Text { get; init; }
        public required string SemanticTokens { get; init; }
        public required string SourceToTree { get; init; }
        public required string TreeToSource { get; init; }
        public required string TreeToTree { get; init; }
    }
}
