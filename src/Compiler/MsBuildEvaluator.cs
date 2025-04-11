using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using System.Runtime.CompilerServices;
using System.Xml;

namespace DotNetLab;

internal static class MsBuildEvaluator
{
    public static string Evaluate(string projectXml)
    {
        foreach (string envVar in Environment.GetEnvironmentVariables().Keys)
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }

        var msBuildPath = Path.Join(Path.GetTempPath(), "MSBuild.exe");
        File.WriteAllBytes(msBuildPath, []);

        Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", msBuildPath);

        ref Version engineVersion = ref projectCollectionVersion(null);
        engineVersion = new Version(17, 0, 0, 0);

        var collection = new ProjectCollection(
            globalProperties: null,
            loggers: null,
            remoteLoggers: null,
            toolsetDefinitionLocations: ToolsetDefinitionLocations.Default,
            maxNodeCount: 1,
            onlyLogCriticalEvents: false,
            loadProjectsReadOnly: true,
            useAsynchronousLogging: false,
            reuseProjectRootElementCache: false);

        var xmlReader = XmlReader.Create(new StringReader(projectXml));
        var projectRoot = ProjectRootElement.Create(xmlReader, collection);
        projectRoot.FullPath = Path.GetFullPath("project.proj");

        var project = ProjectInstance.FromProjectRootElement(
            projectRoot,
            new ProjectOptions { ProjectCollection = collection });

        return $"""
            <Project>
              <PropertyGroup>{join(project.Properties.Select(p => $"    <{p.Name}>{p.EvaluatedValue}</{p.Name}>"))}
              </PropertyGroup>
              <ItemGroup>{join(project.Items.Select(i => $"""    <{i.ItemType} Include="{i.EvaluatedInclude}" />"""))}
              </ItemGroup>
            </Project>

            """;

        static string join(IEnumerable<string> items)
        {
            var result = string.Join(Environment.NewLine, items);
            return result.Length > 0 ? Environment.NewLine + result : result;
        }

        [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_engineVersion")]
        extern static ref Version projectCollectionVersion(ProjectCollection? _);
    }
}
