using AwesomeAssertions;
using DotNetLab.Infrastructure.Worker;

namespace DotNetLab;

[TestClass]
public sealed class WorkerHostResultTests
{
    [TestMethod]
    public void ReadResult_Empty_IsDefaultForNoOutput()
    {
        var incoming = new WorkerOutputMessage.Empty
        {
            Id = 10,
            InputType = nameof(WorkerInputMessage.OnCachedCompilationLoaded),
        };

        WorkerHost.ReadResult<NoOutput>(incoming).Should().BeNull();
    }

    [TestMethod]
    public void ReadResult_SuccessNull_IsDefault()
    {
        var incoming = new WorkerOutputMessage.Success(null)
        {
            Id = 1,
            InputType = nameof(WorkerInputMessage.ProvideSemanticTokens),
        };

        WorkerHost.ReadResult<string?>(incoming).Should().BeNull();
    }

    [TestMethod]
    public void ReadResult_Failure_Throws()
    {
        var incoming = new WorkerOutputMessage.Failure("nope")
        {
            Id = 1,
            InputType = "Compile",
        };

        var act = () => WorkerHost.ReadResult<CompiledAssembly>(incoming);
        act.Should().Throw<InvalidOperationException>().WithMessage("nope");
    }
}
