using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetLab;

[TestClass]
public sealed class WorkerServicesTests
{
    [TestMethod]
    public async Task InProcessFactoryExecutesMessage()
    {
        var appServices = new ServiceCollection();
        appServices.AddDotNetLabInProcessWorker();
        using var appServiceProvider = appServices.BuildServiceProvider();

        var factory = appServiceProvider.GetRequiredService<IInProcessWorkerFactory>();
        var workerServiceProvider = factory.Create("http://localhost", LogLevel.Debug);
        using var workerServices = workerServiceProvider as IDisposable;
        var executor = workerServiceProvider.GetRequiredService<WorkerInputMessage.IExecutor>();

        var output = await new WorkerInputMessage.Ping { Id = 42 }.HandleAndGetOutputAsync(executor);

        var success = output as WorkerOutputMessage.Success;
        Assert.IsNotNull(success);
        Assert.IsInstanceOfType<PingResult>(success.Result);
        Assert.AreEqual(42, success.Id);
    }
}
