using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using System.Numerics;
using System.Reflection.Emit;
using System.Security.Cryptography;

namespace DotNetLab;

public static class CodeAnalysisUtil
{
    private static EmitOptions DefaultEmitOptions => field ??= new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb);

    extension(RoslynCompletionItem item)
    {
        public static string InsertionTextPropertyName => "InsertionText";

        public RoslynCompletionItem WithProperty(string name, string value)
        {
            return item.WithProperties(item.Properties.SetItem(name, value));
        }

        public RoslynCompletionItem WithInsertionText(string value)
        {
            return item.WithProperty(RoslynCompletionItem.InsertionTextPropertyName, value);
        }
    }

    extension(EmitOptions)
    {
        public static EmitOptions Default => DefaultEmitOptions;
    }

    extension(SourceText text)
    {
#pragma warning disable RSEXPERIMENTAL003 // 'SyntaxTokenParser' is experimental
        public SyntaxTokenParser CreateTokenizer()
        {
            return SyntaxFactory.CreateTokenParser(text, Compiler.CreateDefaultParseOptions());
        }
#pragma warning restore RSEXPERIMENTAL003 // 'SyntaxTokenParser' is experimental
    }

    public static bool TryGetHostOutputSafe(
        this GeneratorRunResult result,
        string key,
        [NotNullWhen(returnValue: true)] out object? value)
    {
        if (result.GetType().GetProperty("HostOutputs")?.GetValue(result) is ImmutableDictionary<string, object?> hostOutputs)
        {
            return hostOutputs.TryGetValue(key, out value);
        }

        value = null;
        return false;
    }

    public static DiagnosticData ToDiagnosticData(this Diagnostic d)
    {
        string? filePath = d.Location.SourceTree?.FilePath;
        FileLinePositionSpan lineSpan;

        if (string.IsNullOrEmpty(filePath) &&
            d.Location.GetMappedLineSpan() is { IsValid: true } mappedLineSpan)
        {
            filePath = mappedLineSpan.Path;
            lineSpan = mappedLineSpan;
        }
        else
        {
            lineSpan = d.Location.GetLineSpan();
        }

        return new DiagnosticData(
            FilePath: filePath,
            Severity: d.Severity switch
            {
                DiagnosticSeverity.Error => DiagnosticDataSeverity.Error,
                DiagnosticSeverity.Warning => DiagnosticDataSeverity.Warning,
                _ => DiagnosticDataSeverity.Info,
            },
            Id: d.Id,
            HelpLinkUri: d.Descriptor.HelpLinkUri,
            Message: d.GetMessage(),
            StartLineNumber: lineSpan.StartLinePosition.Line + 1,
            StartColumn: lineSpan.StartLinePosition.Character + 1,
            EndLineNumber: lineSpan.EndLinePosition.Line + 1,
            EndColumn: lineSpan.EndLinePosition.Character + 1
        );
    }
}

internal static class RazorUtil
{
    private static readonly Lazy<Func<Action<object>, IRazorFeature>> configureRazorParserOptionsFactory = new(CreateConfigureRazorParserOptionsFactory);

    private static Func<Action<object>, IRazorFeature> CreateConfigureRazorParserOptionsFactory()
    {
        var asm = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(Guid.NewGuid().ToString()), AssemblyBuilderAccess.Run);
        var mod = asm.DefineDynamicModule("Module");
        var featureInterface = typeof(IConfigureRazorParserOptionsFeature);
        var type = mod.DefineType("ConfigureRazorParserOptions", TypeAttributes.Public, parent: typeof(RazorEngineFeatureBase), interfaces: [featureInterface]);
        var field = type.DefineField("configure", typeof(Action<object>), FieldAttributes.Public);
        var orderProperty = featureInterface.GetProperty(nameof(IConfigureRazorParserOptionsFeature.Order))!;
        var orderPropertyDefined = type.DefineProperty(orderProperty.Name, PropertyAttributes.None, orderProperty.PropertyType, null);
        var orderPropertyGetter = type.DefineMethod(orderProperty.GetMethod!.Name, MethodAttributes.Public | MethodAttributes.Virtual, orderProperty.PropertyType, null);
        var orderPropertyGetterIl = orderPropertyGetter.GetILGenerator();
        orderPropertyGetterIl.Emit(OpCodes.Ldc_I4_0);
        orderPropertyGetterIl.Emit(OpCodes.Ret);
        type.DefineMethodOverride(orderPropertyGetter, orderProperty.GetMethod);
        var configureMethod = featureInterface.GetMethod(nameof(IConfigureRazorParserOptionsFeature.Configure))!;
        var configureMethodDefined = type.DefineMethod(configureMethod.Name, MethodAttributes.Public | MethodAttributes.Virtual, configureMethod.ReturnType, configureMethod.GetParameters().Select(p => p.ParameterType).ToArray());
        var configureMethodIl = configureMethodDefined.GetILGenerator();
        configureMethodIl.Emit(OpCodes.Ldarg_0);
        configureMethodIl.Emit(OpCodes.Ldfld, field);
        configureMethodIl.Emit(OpCodes.Ldarg_1);
        configureMethodIl.Emit(OpCodes.Callvirt, typeof(Action<object>).GetMethod(nameof(Action<object>.Invoke))!);
        configureMethodIl.Emit(OpCodes.Ret);
        type.DefineMethodOverride(configureMethodDefined, configureMethod);
        return (configure) =>
        {
            var feature = Activator.CreateInstance(type.CreateType())!;
            feature.GetType().GetField(field.Name)!.SetValue(feature, configure);
            return (IRazorFeature)feature;
        };
    }

    public static bool TryCreateDefaultTypeNameFeature([NotNullWhen(returnValue: true)] out RazorEngineFeatureBase? result)
    {
        if (typeof(RazorEngineFeatureBase).Assembly
            .GetType("Microsoft.AspNetCore.Razor.Language.DefaultTypeNameFeature")?
            .GetConstructor(Type.EmptyTypes)?
            .Invoke(null) is RazorEngineFeatureBase feature)
        {
            result = feature;
            return true;
        }

        result = null;
        return false;
    }

    public static void ConfigureRazorParserOptionsSafe(this RazorProjectEngineBuilder builder, Action<object> configure)
    {
        builder.Features.Add(configureRazorParserOptionsFactory.Value(configure));
    }

    public static string GenerateScope(string targetName, string relativePath)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(relativePath + targetName);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        int hashed = SHA256.HashData(bytes, hash);
        Debug.Assert(hashed == hash.Length);
        return $"b-{toBase36(hash)}";

        static string toBase36(ReadOnlySpan<byte> hash)
        {
            const string chars = "0123456789abcdefghijklmnopqrstuvwxyz";

            Span<char> result = stackalloc char[10];
            BigInteger dividend = BigInteger.Abs(new BigInteger([.. hash[..9]]));
            for (var i = 0; i < 10; i++)
            {
                dividend = BigInteger.DivRem(dividend, 36, out var remainder);
                result[i] = chars[(int)remainder];
            }

            return new string(result);
        }
    }

    public static RazorCSharpDocument GetCSharpDocumentSafe(this RazorCodeDocument document)
    {
        return document.GetDocumentDataSafe<RazorCSharpDocument>("GetCSharpDocument");
    }

    public static IReadOnlyList<RazorDiagnostic> GetDiagnostics(this RazorCSharpDocument document)
    {
        // Different razor versions return IReadOnlyList vs ImmutableArray,
        // so we need to use reflection to avoid MissingMethodException.
        return (IReadOnlyList<RazorDiagnostic>)document.GetType()
            .GetProperty(nameof(document.Diagnostics))!
            .GetValue(document)!;
    }

    private static T GetDocumentDataSafe<T>(this RazorCodeDocument document, string methodName, string? instanceMethodName = null)
    {
        // GetCSharpDocument and similar extension methods have been turned into instance methods in https://github.com/dotnet/razor/pull/11939.
        if (document.GetType().GetMethod(instanceMethodName ?? methodName, BindingFlags.Instance | BindingFlags.NonPublic) is { } method)
        {
            return (T)method.Invoke(document, [])!;
        }

        return (T)typeof(RazorCodeDocument).Assembly.GetType("Microsoft.AspNetCore.Razor.Language.RazorCodeDocumentExtensions")!
            .GetMethod(methodName, BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [document])!;
    }

    public static DocumentIntermediateNode GetDocumentIntermediateNodeSafe(this RazorCodeDocument document)
    {
        return document.GetDocumentDataSafe<DocumentIntermediateNode>("GetDocumentIntermediateNode", "GetDocumentNode");
    }

    public static string GetGeneratedCode(this RazorCSharpDocument document)
    {
        // There can be either `string GeneratedCode` or `SourceText Text` property.
        // See https://github.com/dotnet/razor/pull/11404.

        var documentType = document.GetType();
        var textProperty = documentType.GetProperty("Text");
        if (textProperty != null)
        {
            return ((SourceText)textProperty.GetValue(document)!).ToString();
        }

        return (string)documentType.GetProperty("GeneratedCode")!.GetValue(document)!;
    }

    public static RazorSyntaxTree GetSyntaxTreeSafe(this RazorCodeDocument document)
    {
        return document.GetDocumentDataSafe<RazorSyntaxTree>("GetSyntaxTree");
    }

    public static IEnumerable<RazorProjectItem> EnumerateItemsSafe(this RazorProjectFileSystem fileSystem, string basePath)
    {
        // EnumerateItems was defined in RazorProject before https://github.com/dotnet/razor/pull/11379,
        // then it has moved into RazorProjectFileSystem. Hence we need reflection to access it.
        return (IEnumerable<RazorProjectItem>)fileSystem.GetType()
            .GetMethod(nameof(fileSystem.EnumerateItems))!
            .Invoke(fileSystem, [basePath])!;
    }

    public static RazorCodeDocument ProcessDeclarationOnlySafe(
        this RazorProjectEngine engine,
        RazorProjectItem projectItem)
    {
        return engine.ProcessSafe(projectItem, nameof(engine.ProcessDeclarationOnly));
    }

    public static RazorCodeDocument ProcessDesignTimeSafe(
        this RazorProjectEngine engine,
        RazorProjectItem projectItem)
    {
        return engine.ProcessSafe(projectItem, nameof(engine.ProcessDesignTime));
    }

    public static RazorCodeDocument ProcessSafe(
        this RazorProjectEngine engine,
        RazorProjectItem projectItem)
    {
        return engine.ProcessSafe(projectItem, nameof(engine.Process));
    }

    private static RazorCodeDocument ProcessSafe(
        this RazorProjectEngine engine,
        RazorProjectItem projectItem,
        string methodName)
    {
        // Newer razor versions take CancellationToken parameter,
        // so we need to use reflection to avoid MissingMethodException.

        var method = engine.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(m => m.Name == methodName &&
                m.GetParameters() is
                [
                { ParameterType.FullName: "Microsoft.AspNetCore.Razor.Language.RazorProjectItem" },
                    .. var rest
                ] &&
                rest.All(static p => p.IsOptional))
            .First();

        return (RazorCodeDocument)method
            .Invoke(engine, [projectItem, .. Enumerable.Repeat<object?>(null, method.GetParameters().Length - 1)])!;
    }

    public static string Serialize(this IntermediateNode node)
    {
        // DebuggerDisplayFormatter merged to IntermediateNodeFormatter in https://github.com/dotnet/razor/pull/11931.

        var assembly = typeof(IntermediateNode).Assembly;
        if (assembly.GetType("Microsoft.AspNetCore.Razor.Language.Intermediate.DebuggerDisplayFormatter") is { } debuggerDisplayFormatter)
        {
            var formatter = Activator.CreateInstance(debuggerDisplayFormatter)!;
            debuggerDisplayFormatter.GetMethod("FormatTree", BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(formatter, [node]);
            return formatter.ToString()!;
        }
        else
        {
            var type = assembly.GetType("Microsoft.AspNetCore.Razor.Language.Intermediate.IntermediateNodeFormatter")!;
            var sb = new StringBuilder();
            var formatter = type.GetConstructors().Single().Invoke([sb, 0, false])!;
            type.GetMethod("FormatTree", BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(formatter, [node]);
            return sb.ToString();
        }
    }

    public static void SetCSharpLanguageVersionSafe(this RazorProjectEngineBuilder builder, LanguageVersion languageVersion)
    {
        // Changed in https://github.com/dotnet/razor/commit/40384334fd4c20180c25b3c88a82d3ca5da07487.
        var asm = builder.GetType().Assembly;
        var method = asm.GetType($"Microsoft.AspNetCore.Razor.Language.{nameof(RazorProjectEngineBuilderExtensions)}")
                ?.GetMethod(nameof(RazorProjectEngineBuilderExtensions.SetCSharpLanguageVersion))
            ?? asm.GetType($"Microsoft.CodeAnalysis.Razor.{nameof(RazorProjectEngineBuilderExtensions)}")
                ?.GetMethod(nameof(RazorProjectEngineBuilderExtensions.SetCSharpLanguageVersion));
        method!.Invoke(null, [builder, languageVersion]);
    }

    public static Diagnostic ToDiagnostic(this RazorDiagnostic d)
    {
        DiagnosticSeverity severity = d.Severity.ToDiagnosticSeverity();

        string message = d.GetMessage();

        var descriptor = new DiagnosticDescriptor(
            id: d.Id,
            title: message,
            messageFormat: message,
            category: "Razor",
            defaultSeverity: severity,
            isEnabledByDefault: true);

        return Diagnostic.Create(
            descriptor,
            location: d.Span.ToLocation());
    }

    public static DiagnosticSeverity ToDiagnosticSeverity(this RazorDiagnosticSeverity severity)
    {
        return severity switch
        {
            RazorDiagnosticSeverity.Error => DiagnosticSeverity.Error,
            RazorDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Info,
        };
    }

    public static Location ToLocation(this SourceSpan span)
    {
        if (span == SourceSpan.Undefined)
        {
            return Location.None;
        }

        return Location.Create(
            filePath: span.FilePath,
            textSpan: span.ToTextSpan(),
            lineSpan: span.ToLinePositionSpan());
    }

    public static LinePositionSpan ToLinePositionSpan(this SourceSpan span)
    {
        var lineCount = span.LineCount < 1 ? 1 : span.LineCount;
        return new LinePositionSpan(
            start: new LinePosition(
                line: span.LineIndex,
                character: span.CharacterIndex),
            end: new LinePosition(
                line: span.LineIndex + lineCount - 1,
                character: span.CharacterIndex + span.Length));
    }

    public static TextSpan ToTextSpan(this SourceSpan span)
    {
        return new TextSpan(span.AbsoluteIndex, span.Length);
    }
}

internal readonly record struct RazorGeneratorResultSafe(object Inner)
{
    public bool TryGetCodeDocument(
        string physicalPath,
        [NotNullWhen(returnValue: true)] out RazorCodeDocument? result)
    {
        var method = Inner.GetType().GetMethod("GetCodeDocument");
        if (method is not null &&
            method.GetParameters() is [{ } param] &&
            param.ParameterType == typeof(string) &&
            method.Invoke(Inner, [physicalPath]) is RazorCodeDocument innerResult)
        {
            result = innerResult;
            return true;
        }

        result = null;
        return false;
    }
}

internal static class MsbuildUtil
{
    public static bool TryConvertStringToBool(ReadOnlySpan<char> text, out bool? result)
    {
        if (text.IsWhiteSpace())
        {
            result = null;
            return true;
        }

        if (IsValidBooleanTrue(text))
        {
            result = true;
            return true;
        }

        if (IsValidBooleanFalse(text))
        {
            result = false;
            return true;
        }

        result = null;
        return false;
    }

    public static bool IsValidBooleanTrue(ReadOnlySpan<char> text)
    {
        return text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("!false", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("!off", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("!no", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidBooleanFalse(ReadOnlySpan<char> text)
    {
        return text.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("!true", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("!on", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("!yes", StringComparison.OrdinalIgnoreCase);
    }
}
