using AwesomeAssertions;
using DotNetLab.Features.Compilation;
using DotNetLab.Lab;

namespace DotNetLab;

[TestClass]
public sealed class CompilationOptionsStateTests
{
    [TestMethod]
    public void FromSavedState_CopiesEnumsAndFlags()
    {
        var saved = SavedState.CSharp with
        {
            RazorToolchain = RazorToolchain.InternalApi,
            RazorStrategy = RazorStrategy.DesignTime,
            ShowSymbols = SymbolDisplayKinds.Both,
            ShowOperations = true,
            ShowBoundNodes = true,
            ShowDeclarationDocument = true,
            DecodeCustomAttributeBlobs = true,
            ShowSequencePoints = true,
            FullIl = true,
            ExcludeSingleFileNameInDiagnostics = false,
            IncludeHiddenDiagnostics = true,
        };

        var options = CompilationOptionsState.FromSavedState(saved);

        options.RazorToolchain.Should().Be(RazorToolchain.InternalApi);
        options.RazorStrategy.Should().Be(RazorStrategy.DesignTime);
        options.ShowSymbols.Should().Be(SymbolDisplayKinds.Both);
        options.ShowOperations.Should().BeTrue();
        options.ShowBoundNodes.Should().BeTrue();
        options.ShowDeclarationDocument.Should().BeTrue();
        options.DecodeCustomAttributeBlobs.Should().BeTrue();
        options.ShowSequencePoints.Should().BeTrue();
        options.FullIl.Should().BeTrue();
        options.ExcludeSingleFileNameInDiagnostics.Should().BeFalse();
        options.IncludeHiddenDiagnostics.Should().BeTrue();
    }

    [TestMethod]
    public void ToPreferences_AndWriteTo_RoundTripSavedState()
    {
        var options = new CompilationOptionsState
        {
            RazorToolchain = RazorToolchain.SourceGenerator,
            RazorStrategy = RazorStrategy.Runtime,
            ShowSymbols = SymbolDisplayKinds.Public,
            ShowOperations = true,
            ExcludeSingleFileNameInDiagnostics = true,
        };

        var preferences = options.ToPreferences();
        preferences.ShowSymbolKinds.Should().Be(SymbolDisplayKinds.Public);
        preferences.ShowOperations.Should().BeTrue();
        preferences.ExcludeSingleFileNameInDiagnostics.Should().BeTrue();

        var written = options.WriteTo(SavedState.CSharp);
        written.RazorToolchain.Should().Be(RazorToolchain.SourceGenerator);
        written.ShowSymbols.Should().Be(SymbolDisplayKinds.Public);
        written.Inputs.Should().BeEquivalentTo(SavedState.CSharp.Inputs);
        CompilationOptionsState.FromSavedState(written).Should().Be(options with { });
    }

    [TestMethod]
    public void Default_MatchesCompilationPreferencesDefault()
    {
        var options = new CompilationOptionsState();
        options.RazorToolchain.Should().Be(RazorToolchain.SourceGeneratorOrInternalApi);
        options.RazorStrategy.Should().Be(RazorStrategy.Runtime);
        options.ShowSymbols.Should().Be(SymbolDisplayKinds.None);
        options.ExcludeSingleFileNameInDiagnostics.Should().Be(CompilationPreferences.Default.ExcludeSingleFileNameInDiagnostics);
        options.ToPreferences().Should().Be(CompilationPreferences.Default);
    }
}
