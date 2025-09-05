using Microsoft.JSInterop;

namespace DotNetLab;

internal static class CustomMonacoTheme
{
    public const string Light = "dotnetlab-light";
    public const string Dark = "dotnetlab-dark";

    public static async Task DefineAsync(IJSRuntime jsRuntime)
    {
        // https://github.com/dotnet/vscode-csharp/blob/adf1a040d73c6cbe72d18c516b2c5ceb59b538e3/themes/vs2019_light.json
        await BlazorMonaco.Editor.Global.DefineTheme(jsRuntime, Light, new()
        {
            Base = BuiltInMonacoTheme.Light,
            Inherit = true,
            Colors = new()
            {
                // Avoid colorizing unmatched brackets as red (which overrides even semantic colors which are more correct).
                { "editorBracketHighlight.unexpectedBracket.foreground", "#222222" }, // same as "punctuation"
            },
            Rules =
            [
                new() { Token = "comment", Foreground = "008000" },
                new() { Token = "excludedCode", Foreground = "808080" },
                new() { Token = "variable", Foreground = "001080" },
                new() { Token = "keyword", Foreground = "0000ff" },
                new() { Token = "keywordControl", Foreground = "af00db" },
                new() { Token = "number", Foreground = "098658" },
                new() { Token = "operator", Foreground = "000000" },
                new() { Token = "operatorOverloaded", Foreground = "000000" },
                new() { Token = "macro", Foreground = "0000ff" },
                new() { Token = "string", Foreground = "a31515" },
                new() { Token = "text", Foreground = "000000" },
                new() { Token = "preprocessorText", Foreground = "a31515" },
                new() { Token = "punctuation", Foreground = "222222" },
                new() { Token = "stringVerbatim", Foreground = "a31515" },
                new() { Token = "stringEscapeCharacter", Foreground = "ff0000" },
                new() { Token = "class", Foreground = "267f99" },
                new() { Token = "recordClassName", Foreground = "267f99" },
                new() { Token = "delegateName", Foreground = "267f99" },
                new() { Token = "enum", Foreground = "267f99" },
                new() { Token = "interface", Foreground = "267f99" },
                new() { Token = "moduleName", Foreground = "222222" },
                new() { Token = "struct", Foreground = "267f99" },
                new() { Token = "recordStructName", Foreground = "267f99" },
                new() { Token = "typeParameter", Foreground = "267f99" },
                new() { Token = "fieldName", Foreground = "222222" },
                new() { Token = "enumMember", Foreground = "222222" },
                new() { Token = "constantName", Foreground = "222222" },
                new() { Token = "parameter", Foreground = "001080" },
                new() { Token = "method", Foreground = "795e26" },
                new() { Token = "extensionMethodName", Foreground = "795e26" },
                new() { Token = "property", Foreground = "222222" },
                new() { Token = "event", Foreground = "222222" },
                new() { Token = "namespace", Foreground = "222222" },
                new() { Token = "label", Foreground = "000000" },
                new() { Token = "xmlDocCommentAttributeName", Foreground = "282828" },
                new() { Token = "xmlDocCommentAttributeQuotes", Foreground = "282828" },
                new() { Token = "xmlDocCommentAttributeValue", Foreground = "282828" },
                new() { Token = "xmlDocCommentCDataSection", Foreground = "808080" },
                new() { Token = "xmlDocCommentComment", Foreground = "008000" },
                new() { Token = "xmlDocCommentDelimiter", Foreground = "808080" },
                new() { Token = "xmlDocCommentEntityReference", Foreground = "008000" },
                new() { Token = "xmlDocCommentName", Foreground = "808080" },
                new() { Token = "xmlDocCommentProcessingInstruction", Foreground = "808080" },
                new() { Token = "xmlDocCommentText", Foreground = "008000" },
                new() { Token = "xmlLiteralAttributeName", Foreground = "ff0000" },
                new() { Token = "xmlLiteralAttributeQuotes", Foreground = "0000ff" },
                new() { Token = "xmlLiteralAttributeValue", Foreground = "0000ff" },
                new() { Token = "xmlLiteralCDataSection", Foreground = "0000ff" },
                new() { Token = "xmlLiteralComment", Foreground = "008000" },
                new() { Token = "xmlLiteralDelimiter", Foreground = "800000" },
                new() { Token = "xmlLiteralEmbeddedExpression", Foreground = "000000" },
                new() { Token = "xmlLiteralEntityReference", Foreground = "ff0000" },
                new() { Token = "xmlLiteralName", Foreground = "800000" },
                new() { Token = "xmlLiteralProcessingInstruction", Foreground = "0000ff" },
                new() { Token = "xmlLiteralText", Foreground = "0000ff" },
                new() { Token = "regexComment", Foreground = "008000" },
                new() { Token = "regexCharacterClass", Foreground = "0073ff" },
                new() { Token = "regexAnchor", Foreground = "ff00c1" },
                new() { Token = "regexQuantifier", Foreground = "ff00c1" },
                new() { Token = "regexGrouping", Foreground = "05c3ba" },
                new() { Token = "regexAlternation", Foreground = "05c3ba" },
                new() { Token = "regexText", Foreground = "800000" },
                new() { Token = "regexSelfEscapedCharacter", Foreground = "800000" },
                new() { Token = "regexOtherEscape", Foreground = "9e5b71" },
                new() { Token = "jsonComment", Foreground = "008000" },
                new() { Token = "jsonNumber", Foreground = "098658" },
                new() { Token = "jsonString", Foreground = "a31515" },
                new() { Token = "jsonKeyword", Foreground = "0000ff" },
                new() { Token = "jsonText", Foreground = "000000" },
                new() { Token = "jsonOperator", Foreground = "000000" },
                new() { Token = "jsonPunctuation", Foreground = "000000" },
                new() { Token = "jsonArray", Foreground = "000000" },
                new() { Token = "jsonObject", Foreground = "000000" },
                new() { Token = "jsonPropertyName", Foreground = "0451a5" },
                new() { Token = "jsonConstructorName", Foreground = "795e26" },
            ],
        });

        // https://github.com/dotnet/vscode-csharp/blob/adf1a040d73c6cbe72d18c516b2c5ceb59b538e3/themes/vs2019_dark.json
        await BlazorMonaco.Editor.Global.DefineTheme(jsRuntime, Dark, new()
        {
            Base = BuiltInMonacoTheme.Dark,
            Inherit = true,
            Colors = new()
            {
                // Avoid colorizing unmatched brackets as red (which overrides even semantic colors which are more correct).
                { "editorBracketHighlight.unexpectedBracket.foreground", "#d4d4d4" }, // same as "punctuation"
            },
            Rules =
            [
                new() { Token = "comment", Foreground = "6a9955" },
                new() { Token = "keyword", Foreground = "569cd6" },
                new() { Token = "keywordControl", Foreground = "c586c0" },
                new() { Token = "number", Foreground = "b5cea8" },
                new() { Token = "operator", Foreground = "d4d4d4" },
                new() { Token = "string", Foreground = "ce9178" },
                new() { Token = "class", Foreground = "4ec9b0" },
                new() { Token = "interface", Foreground = "b8d7a3" },
                new() { Token = "struct", Foreground = "86c691" },
                new() { Token = "enum", Foreground = "b8d7a3" },
                new() { Token = "enumMember", Foreground = "d4d4d4" },
                new() { Token = "delegateName", Foreground = "4ec9b0" },
                new() { Token = "method", Foreground = "dcdcaa" },
                new() { Token = "extensionMethodName", Foreground = "dcdcaa" },
                new() { Token = "preprocessorText", Foreground = "d4d4d4" },
                new() { Token = "xmlDocCommentComment", Foreground = "608b4e" },
                new() { Token = "xmlDocCommentName", Foreground = "569cd6" },
                new() { Token = "xmlDocCommentDelimiter", Foreground = "808080" },
                new() { Token = "xmlDocCommentAttributeName", Foreground = "c8c8c8" },
                new() { Token = "xmlDocCommentAttributeValue", Foreground = "c8c8c8" },
                new() { Token = "xmlDocCommentCDataSection", Foreground = "e9d585" },
                new() { Token = "xmlDocCommentText", Foreground = "608b4e" },
                new() { Token = "punctuation", Foreground = "d4d4d4" },
                new() { Token = "variable", Foreground = "9cdcfe" },
                new() { Token = "property", Foreground = "d4d4d4" },
                new() { Token = "parameter", Foreground = "9cdcfe" },
                new() { Token = "fieldName", Foreground = "d4d4d4" },
                new() { Token = "event", Foreground = "d4d4d4" },
                new() { Token = "namespace", Foreground = "d4d4d4" },
                new() { Token = "typeParameter", Foreground = "b8d7a3" },
                new() { Token = "constantName", Foreground = "d4d4d4" },
                new() { Token = "stringVerbatim", Foreground = "ce9178" },
                new() { Token = "stringEscapeCharacter", Foreground = "d7ba7d" },
                new() { Token = "excludedCode", Foreground = "808080" },
                new() { Token = "macro", Foreground = "808080" },
                new() { Token = "label", Foreground = "c8c8c8" },
                new() { Token = "operatorOverloaded", Foreground = "d4d4d4" },
                new() { Token = "regexComment", Foreground = "57a64a" },
                new() { Token = "regexCharacterClass", Foreground = "2eabfe" },
                new() { Token = "regexAnchor", Foreground = "f979ae" },
                new() { Token = "regexQuantifier", Foreground = "f979ae" },
                new() { Token = "regexGrouping", Foreground = "05c3ba" },
                new() { Token = "regexAlternation", Foreground = "05c3ba" },
                new() { Token = "regexSelfEscapedCharacter", Foreground = "d69d85" },
                new() { Token = "regexOtherEscape", Foreground = "ffd68f" },
                new() { Token = "regexText", Foreground = "d69d85" },
            ],
        });
    }
}

internal static class BuiltInMonacoTheme
{
    public const string Light = "vs";
    public const string Dark = "vs-dark";
}
