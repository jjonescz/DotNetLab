namespace DotNetLab.Features.Documents;

public static class LabFixtures
{
    public const string DefaultProgram = """
        using System;

        public class Program
        {
            public static void Main()
            {
                Console.WriteLine("Hello, .NET Lab!");
            }
        }

        """;

    public const string DirectivesFileName = "Directives.cs";
    public const string ConfigurationFileName = "Configuration.cs";
    public static readonly string[] SpecialSourceOrder = [DirectivesFileName, ConfigurationFileName];

    public const string DefaultRazor = """
        <div>@Param</div>
        @if (Param == 0)
        {
            <TestComponent Param="1" />
        }

        @code {
            [Parameter] public int Param { get; set; }
        }

        """;

    public const string DefaultRazorImports = """
        @using System.Net.Http
        @using System.Net.Http.Json
        @using Microsoft.AspNetCore.Components.Forms
        @using Microsoft.AspNetCore.Components.Routing
        @using Microsoft.AspNetCore.Components.Web
        @using Microsoft.AspNetCore.Components.Web.Virtualization
        @using Microsoft.JSInterop

        """;

    public const string DefaultCshtml = """
        @page
        @using System.ComponentModel.DataAnnotations
        @model PageModel
        @addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers

        <form method="post">
            Name:
            <input asp-for="Customer.Name" />
            <input type="submit" />
        </form>

        @functions {
            public class PageModel
            {
                public Customer Customer { get; set; } = new();
            }

            public class Customer
            {
                public int Id { get; set; }

                [Required, StringLength(10)]
                public string Name { get; set; } = "";
            }
        }

        """;

    public const string DefaultDirectives = """
        #:property AllowUnsafeBlocks=true
        #:property Configuration=Debug
        #:property Features=use-roslyn-tokenizer;FileBasedProgram
        #:property ImplicitUsings=disable
        #:property LangVersion=preview
        #:property Nullable=enable
        // More directives are supported; discover them via IDE suggestions.

        """;

    public const string DefaultConfiguration = """
        // Prefer #: directives in C# files (or Add > Directives), for example:
        //   #:property Configuration=Debug

        Config.CSharpParseOptions(options => options
            .WithLanguageVersion(LanguageVersion.Preview)
            .WithPreprocessorSymbols("DEBUG")
            .WithFeatures(
            [
                new("use-roslyn-tokenizer", "true"),
                new("FileBasedProgram", "true"),
            ])
        );

        Config.CSharpCompilationOptions(options => options
            .WithAllowUnsafe(true)
            .WithNullableContextOptions(NullableContextOptions.Enable)
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithSpecificDiagnosticOptions(
            [
                new("CS1701", ReportDiagnostic.Suppress),
                new("CS1702", ReportDiagnostic.Suppress),
            ])
        );

        Config.EmitOptions(options => options
            .WithEmitMetadataOnly(false)
        );

        """;

    public const string IlOutput = """
        .class private auto ansi '<Module>'
        {
        } // end of class <Module>

        .class public auto ansi beforefieldinit Program
               extends [System.Runtime]System.Object
        {
          .method public hidebysig static
                  void Main () cil managed
          {
            .entrypoint
            IL_0000: ldstr      "Hello, .NET Lab!"
            IL_0005: call       void [System.Console]System.Console::WriteLine(string)
            IL_000a: ret
          }
        }
        """;

    public const string TreeOutput = """
        CompilationUnit
        ├─UsingDirective
        │ └─IdentifierName "System"
        └─ClassDeclaration "Program"
          └─MethodDeclaration "Main"
            └─InvocationExpression
              └─StringLiteralExpression "Hello, .NET Lab!"
        """;

    public const string SeqOutput = """
        Hidden sequence points are omitted.

        Program.Main()
          IL_0000  Program.cs:9
          IL_000a  Program.cs:10
        """;

    public const string DocsOutput = """
        <?xml version="1.0"?>
        <doc>
            <assembly>
                <name>Program</name>
            </assembly>
            <members>
            </members>
        </doc>
        """;

    public const string RazorSyntaxOutput = """
        RazorDocument
        ├─RazorDirective "@page"
        ├─MarkupElement "h1"
        └─MarkupTagHelper "FluentProgressBar"
        """;

    public const string RazorIrOutput = """
        Document
        ├─Namespace TestNamespace
        └─Class TestComponent
          └─Method BuildRenderTree
            ├─OpenElement h1
            └─OpenComponent FluentProgressBar
        """;

    public const string GeneratedRazorCSharp = """
        // <auto-generated/>
        #nullable restore
        namespace TestNamespace
        {
            public partial class TestComponent : global::Microsoft.AspNetCore.Components.ComponentBase
            {
                protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                {
                    __builder.OpenElement(0, "h1");
                    __builder.AddContent(1, "Specimen report");
                    __builder.CloseElement();
                }
            }
        }
        """;

    public const string RazorHtmlOutput = """
        <h1>Specimen report</h1>
        """;

    public const string DecompiledCSharp = """
        using System;
        using System.Diagnostics;
        using System.Reflection;
        using System.Runtime.CompilerServices;
        using System.Security;
        using System.Security.Permissions;

        [assembly: CompilationRelaxations(8)]
        [assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
        [assembly: Debuggable(DebuggableAttribute.DebuggingModes.Default | DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints | DebuggableAttribute.DebuggingModes.EnableEditAndContinue | DebuggableAttribute.DebuggingModes.DisableOptimizations)]
        [assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
        [assembly: AssemblyVersion("0.0.0.0")]
        [module: UnverifiableCode]
        [module: RefSafetyRules(11)]
        [CompilerGenerated]
        internal class Program
        {
        	private static void Main(string[] args)
        	{
        		Console.WriteLine("Hello, .NET Lab!");
        	}
        }
        """;
}
