using AwesomeAssertions;
using DotNetLab.Editor;
using DotNetLab.Features.Compilation;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Workspace;
using DotNetLab.Lab;
using DotNetLab.Shell.StatusBar;

namespace DotNetLab;

[TestClass]
public sealed class CompileGenerationTests
{
    [TestMethod]
    public void GenerationCounter_StaleSnapshotsLose()
    {
        var counter = new GenerationCounter();
        counter.Current.Should().Be(0);

        var first = counter.Begin();
        first.Should().Be(1);
        counter.IsCurrent(first).Should().BeTrue();

        var second = counter.Begin();
        second.Should().Be(2);
        counter.IsCurrent(first).Should().BeFalse();
        counter.IsCurrent(second).Should().BeTrue();
    }

    [TestMethod]
    public void CompilerApplyGenerations_RoslynStaysCurrentWhenRazorBegins()
    {
        var generations = new CompilerApplyGenerations();
        var roslyn = generations.Begin(CompilerKind.Roslyn);
        var razor = generations.Begin(CompilerKind.Razor);

        generations.IsCurrent(CompilerKind.Roslyn, roslyn).Should().BeTrue();
        generations.IsCurrent(CompilerKind.Razor, razor).Should().BeTrue();

        var nextRoslyn = generations.Begin(CompilerKind.Roslyn);
        generations.IsCurrent(CompilerKind.Roslyn, roslyn).Should().BeFalse();
        generations.IsCurrent(CompilerKind.Roslyn, nextRoslyn).Should().BeTrue();
        generations.IsCurrent(CompilerKind.Razor, razor).Should().BeTrue();
    }

    [TestMethod]
    public void CompilerApplyGenerations_SdkIsIndependentOfCompilers()
    {
        var generations = new CompilerApplyGenerations();
        var sdk = generations.BeginSdk();
        _ = generations.Begin(CompilerKind.Roslyn);
        _ = generations.Begin(CompilerKind.Razor);

        generations.IsCurrentSdk(sdk).Should().BeTrue();
        generations.IsCurrentSdk(generations.BeginSdk()).Should().BeTrue();
        generations.IsCurrentSdk(sdk).Should().BeFalse();
    }

    [TestMethod]
    public void CompilationReducers_SetRunningAndStale()
    {
        var state = new CompilationState();
        state.Stale.Should().BeTrue();
        state.Running.Should().BeFalse();

        CompilationReducers.Reduce(state, new SetRunningAction(true)).Running.Should().BeTrue();
        CompilationReducers.Reduce(state, new SetStaleAction(false)).Stale.Should().BeFalse();
    }

    [TestMethod]
    public void CompilationReducers_CompilerActionsMarkStale()
    {
        var fresh = new CompilationState { Stale = false };
        CompilationReducers.Reduce(fresh, new ApplySdkAction("10.0")).Stale.Should().BeTrue();
        CompilationReducers.Reduce(fresh, new SdkApplyStartedAction("10.0")).Stale.Should().BeTrue();
        CompilationReducers.Reduce(fresh, new SdkResolvedAction("10.0")).Stale.Should().BeTrue();
        CompilationReducers.Reduce(fresh, new RestoreCompilersAction("built-in", "built-in", "Release", "built-in", "Release")).Stale.Should().BeTrue();
        CompilationReducers.Reduce(fresh, new CompilerApplyStartedAction(CompilerKind.Roslyn, "built-in", "Release")).Stale.Should().BeTrue();
        CompilationReducers.Reduce(new CompilationState { Stale = true }, new ApplySdkAction("10.0")).Stale.Should().BeTrue();
    }

    [TestMethod]
    public void CompilationReducers_SetDiagnosticCounts()
    {
        var updated = CompilationReducers.Reduce(
            new CompilationState(),
            new SetDiagnosticCountsAction(2, 3));
        updated.ErrorCount.Should().Be(2);
        updated.WarningCount.Should().Be(3);
        updated.HasDiagnosticCounts.Should().BeTrue();
        new CompilationState().HasDiagnosticCounts.Should().BeFalse();
    }

    [TestMethod]
    public void EditorCursor_SetRaisesChangedOnce()
    {
        var cursor = new EditorCursor();
        var count = 0;
        cursor.Changed += () => count++;

        cursor.Set(9, 34);
        count.Should().Be(0);

        cursor.Set(3, 5);
        cursor.Line.Should().Be(3);
        cursor.Column.Should().Be(5);
        count.Should().Be(1);

        cursor.Set(3, 5);
        count.Should().Be(1);
    }

    [TestMethod]
    public void StatusSelectors_SourceAndOutput()
    {
        var stale = new CompilationState { Stale = true };
        StatusSelectors.SourceRight(stale).Should().Contain("Modified");
        StatusSelectors.SourceReady(stale).Should().BeFalse();
        StatusSelectors.SourceReady(new CompilationState { Stale = false }).Should().BeTrue();
        StatusSelectors.OutputReady(new CompilationState { Running = true }).Should().BeFalse();

        var compiler = new CompilerState { Sdk = "built-in", Roslyn = "built-in" };
        StatusSelectors.OutputRight(compiler).Should().Contain(".NET");

        StatusSelectors.Left("source", "C#", "Program.cs", 3, 5, new CompilationState { ErrorCount = 1, WarningCount = 2 }).Should().Equal(
            "Ln 3, Col 5", "Spaces: 4", "UTF-8", "C#", "1 error", "2 warnings");
        StatusSelectors.Left("output", "C#", "Program.cs", 3, 5, new CompilationState { WarningCount = 1 }).Should().Equal(
            "Program.cs", "1 warning");
    }
}
