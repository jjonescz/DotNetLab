using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab.Infrastructure.Worker;

/// <summary>
/// Lets a host add services to the in-process compiler container
/// (<see cref="WorkerServices"/>). Native apps register
/// <c>IJitAsmDisassembler</c> here. Browser WASM sets
/// <c>AssembliesAreAlwaysInDllFormat</c> because the host serves DLLs.
/// </summary>
public interface IWorkerConfigurer
{
    void ConfigureWorkerServices(ServiceCollection services);
}

internal sealed class NoopWorkerConfigurer : IWorkerConfigurer
{
    public void ConfigureWorkerServices(ServiceCollection services)
    {
    }
}
