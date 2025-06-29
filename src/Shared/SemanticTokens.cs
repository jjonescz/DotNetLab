using System.Collections.Frozen;

namespace DotNetLab;

public static class SemanticTokensUtil
{
    private static readonly Lazy<LspIndexedMap> tokenTypes = new(CreateTokenTypes);
    private static readonly Lazy<LspIndexedMap> tokenModifiers = new(CreateTokenModifiers);

    public static LspIndexedMap TokenTypes => tokenTypes.Value;

    public static LspIndexedMap TokenModifiers => tokenModifiers.Value;

    private static LspIndexedMap CreateTokenTypes()
    {
        return new LspIndexedMap(
            ClassificationTypeNames.AllTypeNames
                .Where(static name => !ClassificationTypeNames.AdditiveTypeNames.Contains(name)),
            MapFromRoslynToLspType);
    }

    private static LspIndexedMap CreateTokenModifiers()
    {
        return new LspIndexedMap(
            ClassificationTypeNames.AdditiveTypeNames,
            MapFromRoslynToLspModifier);
    }

    private static string? MapFromRoslynToLspType(string name)
    {
        return name switch
        {
            // https://github.com/dotnet/roslyn/blob/7c625024a1984d9f04f317940d518402f5898758/src/LanguageServer/Protocol/Handler/SemanticTokens/SemanticTokensSchema.cs#L23

            ClassificationTypeNames.Comment => "comment",
            ClassificationTypeNames.Identifier => "variable",
            ClassificationTypeNames.Keyword => "keyword",
            ClassificationTypeNames.NumericLiteral => "number",
            ClassificationTypeNames.Operator => "operator",
            ClassificationTypeNames.StringLiteral => "string",

            // https://github.com/dotnet/roslyn/blob/7c625024a1984d9f04f317940d518402f5898758/src/LanguageServer/Protocol/Handler/SemanticTokens/SemanticTokensSchema.cs#L38

            ClassificationTypeNames.ClassName => "class",
            ClassificationTypeNames.StructName => "struct",
            ClassificationTypeNames.NamespaceName => "namespace",
            ClassificationTypeNames.EnumName => "enum",
            ClassificationTypeNames.InterfaceName => "interface",
            ClassificationTypeNames.TypeParameterName => "typeParameter",
            ClassificationTypeNames.ParameterName => "parameter",
            ClassificationTypeNames.LocalName => "variable",
            ClassificationTypeNames.PropertyName => "property",
            ClassificationTypeNames.MethodName => "method",
            ClassificationTypeNames.EnumMemberName => "enumMember",
            ClassificationTypeNames.EventName => "event",
            ClassificationTypeNames.PreprocessorKeyword => "macro",
            ClassificationTypeNames.LabelName => "label",

            // https://github.com/dotnet/roslyn/blob/f8eb8e56f27fd231dfee95d1a17a2eca7e423d42/src/LanguageServer/Protocol/Handler/SemanticTokens/CustomLspSemanticTokenNames.cs#L84

            ClassificationTypeNames.ExcludedCode => "excludedCode",
            ClassificationTypeNames.ControlKeyword => "keywordControl",
            ClassificationTypeNames.OperatorOverloaded => "operatorOverloaded",
            ClassificationTypeNames.WhiteSpace => "whitespace",
            ClassificationTypeNames.Text => "text",
            ClassificationTypeNames.PreprocessorText => "preprocessorText",
            ClassificationTypeNames.Punctuation => "punctuation",
            ClassificationTypeNames.VerbatimStringLiteral => "stringVerbatim",
            ClassificationTypeNames.StringEscapeCharacter => "stringEscapeCharacter",
            ClassificationTypeNames.RecordClassName => "recordClassName",
            ClassificationTypeNames.DelegateName => "delegateName",
            ClassificationTypeNames.ModuleName => "moduleName",
            ClassificationTypeNames.RecordStructName => "recordStructName",
            ClassificationTypeNames.FieldName => "fieldName",
            ClassificationTypeNames.ConstantName => "constantName",
            ClassificationTypeNames.ExtensionMethodName => "extensionMethodName",

            ClassificationTypeNames.XmlDocCommentAttributeName => "xmlDocCommentAttributeName",
            ClassificationTypeNames.XmlDocCommentAttributeQuotes => "xmlDocCommentAttributeQuotes",
            ClassificationTypeNames.XmlDocCommentAttributeValue => "xmlDocCommentAttributeValue",
            ClassificationTypeNames.XmlDocCommentCDataSection => "xmlDocCommentCDataSection",
            ClassificationTypeNames.XmlDocCommentComment => "xmlDocCommentComment",
            ClassificationTypeNames.XmlDocCommentDelimiter => "xmlDocCommentDelimiter",
            ClassificationTypeNames.XmlDocCommentEntityReference => "xmlDocCommentEntityReference",
            ClassificationTypeNames.XmlDocCommentName => "xmlDocCommentName",
            ClassificationTypeNames.XmlDocCommentProcessingInstruction => "xmlDocCommentProcessingInstruction",
            ClassificationTypeNames.XmlDocCommentText => "xmlDocCommentText",

            ClassificationTypeNames.XmlLiteralAttributeName => "xmlLiteralAttributeName",
            ClassificationTypeNames.XmlLiteralAttributeQuotes => "xmlLiteralAttributeQuotes",
            ClassificationTypeNames.XmlLiteralAttributeValue => "xmlLiteralAttributeValue",
            ClassificationTypeNames.XmlLiteralCDataSection => "xmlLiteralCDataSection",
            ClassificationTypeNames.XmlLiteralComment => "xmlLiteralComment",
            ClassificationTypeNames.XmlLiteralDelimiter => "xmlLiteralDelimiter",
            ClassificationTypeNames.XmlLiteralEmbeddedExpression => "xmlLiteralEmbeddedExpression",
            ClassificationTypeNames.XmlLiteralEntityReference => "xmlLiteralEntityReference",
            ClassificationTypeNames.XmlLiteralName => "xmlLiteralName",
            ClassificationTypeNames.XmlLiteralProcessingInstruction => "xmlLiteralProcessingInstruction",
            ClassificationTypeNames.XmlLiteralText => "xmlLiteralText",

            ClassificationTypeNames.RegexComment => "regexComment",
            ClassificationTypeNames.RegexCharacterClass => "regexCharacterClass",
            ClassificationTypeNames.RegexAnchor => "regexAnchor",
            ClassificationTypeNames.RegexQuantifier => "regexQuantifier",
            ClassificationTypeNames.RegexGrouping => "regexGrouping",
            ClassificationTypeNames.RegexAlternation => "regexAlternation",
            ClassificationTypeNames.RegexText => "regexText",
            ClassificationTypeNames.RegexSelfEscapedCharacter => "regexSelfEscapedCharacter",
            ClassificationTypeNames.RegexOtherEscape => "regexOtherEscape",

            ClassificationTypeNames.JsonComment => "jsonComment",
            ClassificationTypeNames.JsonNumber => "jsonNumber",
            ClassificationTypeNames.JsonString => "jsonString",
            ClassificationTypeNames.JsonKeyword => "jsonKeyword",
            ClassificationTypeNames.JsonText => "jsonText",
            ClassificationTypeNames.JsonOperator => "jsonOperator",
            ClassificationTypeNames.JsonPunctuation => "jsonPunctuation",
            ClassificationTypeNames.JsonArray => "jsonArray",
            ClassificationTypeNames.JsonObject => "jsonObject",
            ClassificationTypeNames.JsonPropertyName => "jsonPropertyName",
            ClassificationTypeNames.JsonConstructorName => "jsonConstructorName",

            ClassificationTypeNames.TestCodeMarkdown => "testCodeMarkdown",

            _ => null,
        };
    }

    private static string? MapFromRoslynToLspModifier(string name)
    {
        return name switch
        {
            ClassificationTypeNames.StaticSymbol => "static",
            ClassificationTypeNames.ReassignedVariable => "reassignedVariable",
            ClassificationTypeNames.ObsoleteSymbol => "deprecated",
            _ => null,
        };
    }
}

public sealed class LspIndexedMap
{
    public LspIndexedMap(IEnumerable<string> roslynKeys, Func<string, string?> roslynToLspMapping)
    {
        var collection = roslynKeys
            .Select(roslynKey => (roslynKey, lspValue: roslynToLspMapping(roslynKey)))
            .Where(static t => t.lspValue != null)
            .Select(static (t, index) => (t.roslynKey, t.lspValue, index));

        LspValues = collection
            .Select(static t => t.lspValue!)
            .ToImmutableArray();

        RoslynToLspIndexMap = collection
            .ToFrozenDictionary(
                static t => t.roslynKey,
                static t => t.index);
    }

    public ImmutableArray<string> LspValues { get; }
    public FrozenDictionary<string, int> RoslynToLspIndexMap { get; }
}

internal static class ClassificationTypeNames
{
    public static ImmutableArray<string> AdditiveTypeNames { get; } = [StaticSymbol, ReassignedVariable, ObsoleteSymbol, TestCode];

    public static ImmutableArray<string> AllTypeNames { get; } =
    [
        Comment,
        ExcludedCode,
        Identifier,
        Keyword,
        ControlKeyword,
        NumericLiteral,
        Operator,
        OperatorOverloaded,
        PreprocessorKeyword,
        StringLiteral,
        WhiteSpace,
        Text,
        ReassignedVariable,
        ObsoleteSymbol,
        StaticSymbol,
        PreprocessorText,
        Punctuation,
        VerbatimStringLiteral,
        StringEscapeCharacter,
        ClassName,
        RecordClassName,
        DelegateName,
        EnumName,
        InterfaceName,
        ModuleName,
        StructName,
        RecordStructName,
        TypeParameterName,
        FieldName,
        EnumMemberName,
        ConstantName,
        LocalName,
        ParameterName,
        MethodName,
        ExtensionMethodName,
        PropertyName,
        EventName,
        NamespaceName,
        LabelName,
        XmlDocCommentAttributeName,
        XmlDocCommentAttributeQuotes,
        XmlDocCommentAttributeValue,
        XmlDocCommentCDataSection,
        XmlDocCommentComment,
        XmlDocCommentDelimiter,
        XmlDocCommentEntityReference,
        XmlDocCommentName,
        XmlDocCommentProcessingInstruction,
        XmlDocCommentText,
        XmlLiteralAttributeName,
        XmlLiteralAttributeQuotes,
        XmlLiteralAttributeValue,
        XmlLiteralCDataSection,
        XmlLiteralComment,
        XmlLiteralDelimiter,
        XmlLiteralEmbeddedExpression,
        XmlLiteralEntityReference,
        XmlLiteralName,
        XmlLiteralProcessingInstruction,
        XmlLiteralText,
        RegexComment,
        RegexCharacterClass,
        RegexAnchor,
        RegexQuantifier,
        RegexGrouping,
        RegexAlternation,
        RegexText,
        RegexSelfEscapedCharacter,
        RegexOtherEscape,
        JsonComment,
        JsonNumber,
        JsonString,
        JsonKeyword,
        JsonText,
        JsonOperator,
        JsonPunctuation,
        JsonArray,
        JsonObject,
        JsonPropertyName,
        JsonConstructorName,
        TestCode,
        TestCodeMarkdown,
    ];

    public const string Comment = "comment";
    public const string ExcludedCode = "excluded code";
    public const string Identifier = "identifier";
    public const string Keyword = "keyword";
    public const string ControlKeyword = "keyword - control";
    public const string NumericLiteral = "number";
    public const string Operator = "operator";
    public const string OperatorOverloaded = "operator - overloaded";
    public const string PreprocessorKeyword = "preprocessor keyword";
    public const string StringLiteral = "string";
    public const string WhiteSpace = "whitespace";
    public const string Text = "text";

    public const string ReassignedVariable = "reassigned variable";
    public const string ObsoleteSymbol = "obsolete symbol";
    public const string StaticSymbol = "static symbol";

    public const string PreprocessorText = "preprocessor text";
    public const string Punctuation = "punctuation";
    public const string VerbatimStringLiteral = "string - verbatim";
    public const string StringEscapeCharacter = "string - escape character";

    public const string ClassName = "class name";
    public const string RecordClassName = "record class name";
    public const string DelegateName = "delegate name";
    public const string EnumName = "enum name";
    public const string InterfaceName = "interface name";
    public const string ModuleName = "module name";
    public const string StructName = "struct name";
    public const string RecordStructName = "record struct name";
    public const string TypeParameterName = "type parameter name";

    public const string TestCode = "roslyn test code";
    public const string TestCodeMarkdown = "roslyn test code markdown";

    public const string FieldName = "field name";
    public const string EnumMemberName = "enum member name";
    public const string ConstantName = "constant name";
    public const string LocalName = "local name";
    public const string ParameterName = "parameter name";
    public const string MethodName = "method name";
    public const string ExtensionMethodName = "extension method name";
    public const string PropertyName = "property name";
    public const string EventName = "event name";
    public const string NamespaceName = "namespace name";
    public const string LabelName = "label name";

    public const string XmlDocCommentAttributeName = "xml doc comment - attribute name";
    public const string XmlDocCommentAttributeQuotes = "xml doc comment - attribute quotes";
    public const string XmlDocCommentAttributeValue = "xml doc comment - attribute value";
    public const string XmlDocCommentCDataSection = "xml doc comment - cdata section";
    public const string XmlDocCommentComment = "xml doc comment - comment";
    public const string XmlDocCommentDelimiter = "xml doc comment - delimiter";
    public const string XmlDocCommentEntityReference = "xml doc comment - entity reference";
    public const string XmlDocCommentName = "xml doc comment - name";
    public const string XmlDocCommentProcessingInstruction = "xml doc comment - processing instruction";
    public const string XmlDocCommentText = "xml doc comment - text";

    public const string XmlLiteralAttributeName = "xml literal - attribute name";
    public const string XmlLiteralAttributeQuotes = "xml literal - attribute quotes";
    public const string XmlLiteralAttributeValue = "xml literal - attribute value";
    public const string XmlLiteralCDataSection = "xml literal - cdata section";
    public const string XmlLiteralComment = "xml literal - comment";
    public const string XmlLiteralDelimiter = "xml literal - delimiter";
    public const string XmlLiteralEmbeddedExpression = "xml literal - embedded expression";
    public const string XmlLiteralEntityReference = "xml literal - entity reference";
    public const string XmlLiteralName = "xml literal - name";
    public const string XmlLiteralProcessingInstruction = "xml literal - processing instruction";
    public const string XmlLiteralText = "xml literal - text";

    public const string RegexComment = "regex - comment";
    public const string RegexCharacterClass = "regex - character class";
    public const string RegexAnchor = "regex - anchor";
    public const string RegexQuantifier = "regex - quantifier";
    public const string RegexGrouping = "regex - grouping";
    public const string RegexAlternation = "regex - alternation";
    public const string RegexText = "regex - text";
    public const string RegexSelfEscapedCharacter = "regex - self escaped character";
    public const string RegexOtherEscape = "regex - other escape";

    public const string JsonComment = "json - comment";
    public const string JsonNumber = "json - number";
    public const string JsonString = "json - string";
    public const string JsonKeyword = "json - keyword";
    public const string JsonText = "json - text";
    public const string JsonOperator = "json - operator";
    public const string JsonPunctuation = "json - punctuation";
    public const string JsonArray = "json - array";
    public const string JsonObject = "json - object";
    public const string JsonPropertyName = "json - property name";
    public const string JsonConstructorName = "json - constructor name";
}
