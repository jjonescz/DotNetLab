using ILCompiler;
using Internal.IL;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;
using System.Reflection.PortableExecutable;

namespace DotNetLab;

public static class Disassembler
{
    private static readonly Type moduleDataType = typeof(CompilerTypeSystemContext).GetNestedType("ModuleData", BindingFlags.NonPublic)!;
    private static readonly FieldInfo moduleDataModuleField = moduleDataType.GetField("Module")!;
    private static readonly MethodInfo addModuleMethod = typeof(CompilerTypeSystemContext).GetMethod("AddModule", BindingFlags.NonPublic | BindingFlags.Instance)!;

    public static string Disassemble(MemoryStream emitStream, ImmutableArray<RefAssembly> references)
    {
        var sb = new StringBuilder();

        try
        {
            var targetDetails = new TargetDetails(TargetArchitecture.X64, TargetOS.Windows, TargetAbi.Unknown);

            var typeSystemContext = new CompilerTypeSystemContext(targetDetails, SharedGenericsMode.Disabled, DelegateFeature.All);

            typeSystemContext.AddModule(emitStream);

            foreach (var r in references)
            {
                typeSystemContext.AddModule(r.Bytes);
            }

            typeSystemContext.SetSystemModule(typeSystemContext.GetModuleForSimpleName("mscorlib"));

            var compilationGroup = new SingleFileCompilationModuleGroup();
            var builder = new RyuJitCompilationBuilder(typeSystemContext, compilationGroup);
            var compilation = builder.ToCompilation();
            var results = compilation.Compile("output.obj", new XmlObjectDumper("output.xml"));

            sb.AppendLine("; START output.xml");
            sb.AppendLine(File.ReadAllText("output.xml"));
            sb.AppendLine("; END output.xml");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"; Error: {ex}");
        }

        return sb.ToString();
    }

    private static void AddModule(this CompilerTypeSystemContext typeSystemContext, Stream peStream)
    {
        using var peReader = new PEReader(peStream);
        typeSystemContext.AddModule(peReader);
    }

    private static void AddModule(this CompilerTypeSystemContext typeSystemContext, ImmutableArray<byte> bytes)
    {
        using var peReader = new PEReader(bytes);
        typeSystemContext.AddModule(peReader);
    }

    private static void AddModule(this CompilerTypeSystemContext typeSystemContext, PEReader peReader)
    {
        var module = EcmaModule.Create(typeSystemContext, peReader, containingAssembly: null);
        var moduleData = Activator.CreateInstance(moduleDataType)!;
        moduleDataModuleField.SetValue(moduleData, module);

        // private EcmaModule AddModule(string filePath, string expectedSimpleName, bool useForBinding, ModuleData oldModuleData = null, bool throwOnFailureToLoad = true)
        addModuleMethod.Invoke(typeSystemContext, [null, null, true, moduleData, true]);
    }
}
