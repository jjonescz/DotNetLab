using AwesomeAssertions;
using DotNetLab.Infrastructure.Browser;
using DotNetLab.Infrastructure.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab;

[TestClass]
public sealed class AddDotNetLabAppTests
{
    [TestMethod]
    public void ResolvesEnvironmentAndHttpClient()
    {
        var services = new ServiceCollection();
        var environment = new LabEnvironment(false, "https://example.test/");
        services.AddDotNetLabApp(environment);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<LabEnvironment>().Should().BeSameAs(environment);
        environment.SupportsThreads.Should().BeFalse();

        using var client = provider.GetRequiredService<HttpClient>();
        client.BaseAddress.Should().Be(new Uri("https://example.test/"));
        client.DefaultRequestHeaders.UserAgent.ToString().Should().Contain("DotNetLab");
        provider.GetRequiredService<IWorkerTransport>().Should().BeOfType<UnsupportedWorkerTransport>();
        provider.GetService<IStoreLink>().Should().BeNull();
    }

    [TestMethod]
    public void DoesNotReplaceExistingWorkerTransport()
    {
        var services = new ServiceCollection();
        var environment = new LabEnvironment(true, "https://example.test/");
        services.AddScoped<IWorkerTransport, HostWorkerTransport>();
        services.AddDotNetLabApp(environment);

        WorkerTransports(services).Should().ContainSingle()
            .Which.ImplementationType.Should().Be(typeof(HostWorkerTransport));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IWorkerTransport>().Should().BeOfType<HostWorkerTransport>();
    }

    [TestMethod]
    public void HostCanRegisterWorkerTransportAfterFallback()
    {
        var services = new ServiceCollection();
        var environment = new LabEnvironment(true, "https://example.test/");
        services.AddDotNetLabApp(environment);
        services.AddScoped<IWorkerTransport, HostWorkerTransport>();

        WorkerTransports(services).Should().HaveCount(2);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IWorkerTransport>().Should().BeOfType<HostWorkerTransport>();
    }

    [TestMethod]
    public void UnsupportedTransport_CreateWorkerThrows()
    {
        var transport = new UnsupportedWorkerTransport();
        var act = () => transport.CreateWorker("main.js", _ => { }, _ => { });
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Workers are only supported in the browser.");
        transport.SupportsBackgroundWorker.Should().BeFalse();
    }

    private static IEnumerable<ServiceDescriptor> WorkerTransports(IServiceCollection services) =>
        services.Where(d => d.ServiceType == typeof(IWorkerTransport));

    private sealed class HostWorkerTransport : IWorkerTransport
    {
        public bool SupportsBackgroundWorker => true;

        public Task EnsureControllerAsync() => Task.CompletedTask;

        public Task EnsureInProcessInteropAsync() => Task.CompletedTask;

        public IWorkerHandle CreateWorker(string scriptUrl, Action<string> onMessage, Action<string> onError)
            => throw new NotSupportedException();

        public void WorkerReady(IWorkerHandle worker)
        {
        }

        public void PostMessage(IWorkerHandle worker, string message)
        {
        }

        public void PostSideMessage(IWorkerHandle worker, string message)
        {
        }

        public void DisposeWorker(IWorkerHandle worker)
        {
        }

        public void CollectAndDownloadGcDump()
        {
        }
    }
}
