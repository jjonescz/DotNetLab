using AwesomeAssertions;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Outputs;
using DotNetLab.Features.Sharing;
using DotNetLab.Lab;

namespace DotNetLab;

[TestClass]
public sealed class CompilerOutputPluginTests
{
    [TestMethod]
    public void WebAssemblyPlugin_ReplacesUnavailableJitWithNativeAppMessage()
    {
        var plugin = new WebAssemblyCompilerOutputPlugin();
        string? language = "plaintext";
        var text = plugin.GetText(
            outputInfo: null,
            new CompiledFileLazyResult
            {
                Text = "JIT disassembler is not available.",
                Metadata = CompiledFileOutputMetadata.JitAsmUnavailableMessage,
            },
            out var disclaimer,
            ref language);

        disclaimer.Should().Be(OutputDisclaimer.None);
        language.Should().BeNull();
        text.Should().Contain("JIT disassembler is not available on this platform.");
        text.Should().Contain(LabLinks.NativeApps);
    }

    [TestMethod]
    public void WebAssemblyPlugin_KeepsCachedNativeAsm()
    {
        var plugin = new WebAssemblyCompilerOutputPlugin();
        string? language = "plaintext";
        var cached = new CompiledFileOutput
        {
            Type = "asm",
            Label = "Asm",
            Language = "x86",
            EagerText = "mov eax, 1",
        };
        var text = plugin.GetText(
            new OutputInfo
            {
                Output = new CompiledFileOutput
                {
                    Type = "asm",
                    Label = "Asm",
                    EagerText = "JIT disassembler is not available.",
                    Metadata = CompiledFileOutputMetadata.JitAsmUnavailableMessage,
                },
                CachedOutput = cached,
            },
            new CompiledFileLazyResult
            {
                Text = "JIT disassembler is not available.",
                Metadata = CompiledFileOutputMetadata.JitAsmUnavailableMessage,
            },
            out var disclaimer,
            ref language);

        disclaimer.Should().Be(OutputDisclaimer.JitAsmUnavailableUsingCached);
        language.Should().Be("x86");
        text.Should().Be("mov eax, 1");
    }

    [TestMethod]
    public void WebAssemblyPlugin_IgnoresIdenticalCachedText()
    {
        var plugin = new WebAssemblyCompilerOutputPlugin();
        string? language = "plaintext";
        var same = "JIT disassembler is not available.";
        var text = plugin.GetText(
            new OutputInfo
            {
                Output = new CompiledFileOutput { Type = "asm", Label = "Asm", EagerText = same },
                CachedOutput = new CompiledFileOutput { Type = "asm", Label = "Asm", Language = "x86", EagerText = same },
            },
            new CompiledFileLazyResult
            {
                Text = same,
                Metadata = CompiledFileOutputMetadata.JitAsmUnavailableMessage,
            },
            out var disclaimer,
            ref language);

        disclaimer.Should().Be(OutputDisclaimer.None);
        language.Should().BeNull();
        text.Should().Contain(LabLinks.NativeApps);
    }

    [TestMethod]
    public void WebAssemblyPlugin_PassesThroughNormalOutput()
    {
        var plugin = new WebAssemblyCompilerOutputPlugin();
        string? language = "x86";
        var text = plugin.GetText(
            outputInfo: null,
            new CompiledFileLazyResult { Text = "mov eax, 1" },
            out var disclaimer,
            ref language);

        disclaimer.Should().Be(OutputDisclaimer.None);
        language.Should().Be("x86");
        text.Should().Be("mov eax, 1");
    }

    [TestMethod]
    public void OutputLanguage_MapsAsmToX86()
        => LabCatalog.OutputLanguage("asm").Should().Be("x86");
}
