using AwesomeAssertions;
using DotNetLab.Editor.Monaco;
using DotNetLab.Infrastructure.Caching.Compilation;
using DotNetLab.Features.Outputs;
using DotNetLab.Features.Updates;
using DotNetLab.Features.Workspace;
using DotNetLab.Infrastructure.Browser;
using DotNetLab.Infrastructure.Worker;
using DotNetLab.Shell;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab;

[TestClass]
public sealed class AddDotNetLabAppTests
{
    [TestMethod]
    public void RegistersHostNeutralServices()
    {
        var services = new ServiceCollection();
        var environment = new LabEnvironment(true, "https://example.test/");

        services.AddDotNetLabApp(environment);

        HasSingleton<ILabEnvironment>(services).Should().BeTrue();
        HasScoped<LabWorkspaceState>(services).Should().BeTrue();
        HasScoped<LabDialogs>(services).Should().BeTrue();
        HasScoped<BlazorMonacoInterop>(services).Should().BeTrue();
        HasScoped<WorkerHost>(services).Should().BeTrue();
        HasScoped<HttpClient>(services).Should().BeTrue();
        services.Should().Contain(d =>
            d.ServiceType == typeof(IUpdateChecker) &&
            d.ImplementationType == typeof(DisabledUpdateChecker) &&
            d.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(d =>
            d.ServiceType == typeof(IWorkerTransport) &&
            d.ImplementationType == typeof(UnsupportedWorkerTransport) &&
            d.Lifetime == ServiceLifetime.Scoped);
        HasScoped<IWorkerConfigurer>(services).Should().BeTrue();
        HasScoped<ICompilerOutputPlugin>(services).Should().BeTrue();
        HasScoped<ICompilationCache>(services).Should().BeTrue();
    }

    [TestMethod]
    public void ResolvesEnvironmentAndHttpClient()
    {
        var services = new ServiceCollection();
        var environment = new LabEnvironment(false, "https://example.test/");
        services.AddDotNetLabApp(environment);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ILabEnvironment>().Should().BeSameAs(environment);
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

    [TestMethod]
    public void RegistersRootComponents()
    {
        var registered = new List<(Type Type, string Selector)>();
        AppBuilder.RegisterRootComponents((type, selector) => registered.Add((type, selector)));

        registered.Should().Contain((typeof(App), "#app"));
        registered.Should().Contain((typeof(HeadOutlet), "head::after"));
    }

    private static bool HasSingleton<T>(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(T) && d.Lifetime == ServiceLifetime.Singleton);

    private static bool HasScoped<T>(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(T) && d.Lifetime == ServiceLifetime.Scoped);

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
