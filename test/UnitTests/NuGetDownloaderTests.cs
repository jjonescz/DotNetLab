using AwesomeAssertions;
using DotNetLab.Lab;
using System.IO.Compression;

namespace DotNetLab;

[TestClass]
public sealed class NuGetDownloaderTests
{
    [TestMethod]
    public async Task IgnoresDuplicatePathsWhenLoadingDocumentation()
    {
        var package = new MemoryStream();
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "content/README.txt", "readme");
            AddEntry(archive, "content/readme.txt", "readme");
            AddEntry(archive, "lib/net11.0/Test.dll", "assembly");
            AddEntry(archive, "lib/net11.0/Test.xml", "documentation");
        }

        package.Position = 0;
        var assemblies = await NuGetUtil.GetAssembliesFromNupkgAsync(
            package,
            new SingleLevelNuGetDllFilter("lib", 2),
            "Test@1.0.0");

        var assembly = assemblies.Should().ContainSingle().Which;
        assembly.Name.Should().Be("Test");
        Encoding.UTF8.GetString(assembly.Documentation.AsSpan()).Should().Be("documentation");
    }

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}
