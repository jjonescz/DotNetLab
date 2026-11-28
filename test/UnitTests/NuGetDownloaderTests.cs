using AwesomeAssertions;
using DotNetLab.Lab;
using Knapcode.MiniZip;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace DotNetLab;

[TestClass]
public sealed class NuGetDownloaderTests
{
    public required TestContext TestContext { get; set; }

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

    [TestMethod]
    public async Task DefersDocumentationLoadingUntilPrepared()
    {
        var loaded = false;
        var assembly = RefAssemblies.All[0];
        var references = RefAssemblyMetadata.Create(
        [
            new()
            {
                Name = assembly.Name,
                FileName = assembly.FileName,
                Bytes = assembly.Bytes,
                Source = "Test",
                DocumentationLoader = () =>
                {
                    loaded = true;
                    return Task.FromResult(ImmutableCollectionsMarshal.AsImmutableArray(
                        "<doc><members /></doc>"u8.ToArray()));
                },
            },
        ]);

        loaded.Should().BeFalse();
        await RefAssemblyMetadata.PrepareDocumentationAsync(references, TestContext.CancellationToken);
        loaded.Should().BeTrue();
    }

    [TestMethod]
    public async Task MemoizesDocumentationLoadingFailure()
    {
        var attempts = 0;
        var assembly = RefAssemblies.All[0];
        var references = RefAssemblyMetadata.Create(
        [
            new()
            {
                Name = assembly.Name,
                FileName = assembly.FileName,
                Bytes = assembly.Bytes,
                Source = "Test",
                DocumentationLoader = new Func<Task<ImmutableArray<byte>>>(() =>
                {
                    attempts++;
                    return attempts == 1
                        ? Task.FromException<ImmutableArray<byte>>(new IOException("Failed to load docs."))
                        : Task.FromResult(ImmutableCollectionsMarshal.AsImmutableArray(
                            "<doc><members /></doc>"u8.ToArray()));
                }).Memoize(),
            },
        ]);

        var first = await RefAssemblyMetadata.PrepareDocumentationAsync(
            references,
            TestContext.CancellationToken);
        first.Should().ContainSingle()
            .Which.Should().BeOfType<IOException>();

        var second = await RefAssemblyMetadata.PrepareDocumentationAsync(
            references,
            TestContext.CancellationToken);
        second.Should().ContainSingle()
            .Which.Should().BeOfType<IOException>();
        attempts.Should().Be(1);
    }

    [TestMethod]
    public async Task TreatsPackageTimeoutAsDocumentationFailure()
    {
        var assembly = RefAssemblies.All[0];
        var references = RefAssemblyMetadata.Create(
        [
            new()
            {
                Name = assembly.Name,
                FileName = assembly.FileName,
                Bytes = assembly.Bytes,
                Source = "Test",
                DocumentationLoader = () =>
                    Task.FromException<ImmutableArray<byte>>(new TaskCanceledException("Package request timed out.")),
            },
        ]);

        var errors = await RefAssemblyMetadata.PrepareDocumentationAsync(
            references,
            TestContext.CancellationToken);

        errors.Should().ContainSingle()
            .Which.Should().BeOfType<TaskCanceledException>();
    }

    [TestMethod]
    public async Task DeferredDocumentationDoesNotCaptureZipReader()
    {
        var package = new MemoryStream();
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "lib/net11.0/Test.dll", "assembly");
            AddEntry(archive, "lib/net11.0/Test.xml", "documentation");
        }

        package.Position = 0;
        using var reader = new ZipDirectoryReader(package, leaveOpen: true);
        var directory = await reader.ReadAsync();
        var assemblies = await NuGetUtil.GetAssembliesFromNupkgAsync(
            reader,
            directory,
            new SingleLevelNuGetDllFilter("lib", 2),
            "Test@1.0.0",
            _ => Task.FromResult(Encoding.UTF8.GetBytes("documentation")));

        reader.IsDisposed.Should().BeFalse();
        reader.Dispose();
        var assembly = assemblies.Should().ContainSingle().Which;
        var documentation = await assembly.DocumentationLoader!();
        Encoding.UTF8.GetString(documentation.AsSpan()).Should().Be("documentation");
    }

    [TestMethod]
    public async Task MemoizesSuccessfulDocumentationLoad()
    {
        var package = new MemoryStream();
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "lib/net11.0/Test.dll", "assembly");
            AddEntry(archive, "lib/net11.0/Test.xml", "documentation");
        }

        package.Position = 0;
        using var reader = new ZipDirectoryReader(package, leaveOpen: true);
        var directory = await reader.ReadAsync();
        var attempts = 0;
        var assemblies = await NuGetUtil.GetAssembliesFromNupkgAsync(
            reader,
            directory,
            new SingleLevelNuGetDllFilter("lib", 2),
            "Test@1.0.0",
            _ =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult(Encoding.UTF8.GetBytes("documentation"));
            });
        var loader = assemblies.Should().ContainSingle().Which.DocumentationLoader!;

        var results = await Task.WhenAll(loader(), loader());
        Encoding.UTF8.GetString(results[0].AsSpan()).Should().Be("documentation");
        await loader();

        attempts.Should().Be(1);
    }

    [TestMethod]
    public async Task MemoizesFailedDocumentationLoad()
    {
        var package = new MemoryStream();
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "lib/net11.0/Test.dll", "assembly");
            AddEntry(archive, "lib/net11.0/Test.xml", "documentation");
        }

        package.Position = 0;
        using var reader = new ZipDirectoryReader(package, leaveOpen: true);
        var directory = await reader.ReadAsync();
        var attempts = 0;
        var assemblies = await NuGetUtil.GetAssembliesFromNupkgAsync(
            reader,
            directory,
            new SingleLevelNuGetDllFilter("lib", 2),
            "Test@1.0.0",
            _ =>
            {
                return Interlocked.Increment(ref attempts) == 1
                    ? Task.FromException<byte[]>(new IOException("Failed to load docs."))
                    : Task.FromResult(Encoding.UTF8.GetBytes("documentation"));
            });
        var loader = assemblies.Should().ContainSingle().Which.DocumentationLoader!;

        await FluentActions.Invoking(loader).Should().ThrowAsync<IOException>();
        await FluentActions.Invoking(loader).Should().ThrowAsync<IOException>();
        attempts.Should().Be(1);
    }

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}
