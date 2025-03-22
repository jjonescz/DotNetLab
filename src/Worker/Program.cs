using DotNetLab;
using KristofferStrube.Blazor.WebWorkers;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;
using System.Text.Json;

Console.WriteLine("Worker started.");

var services = WorkerServices.Create(
    baseUrl: args[0],
    debugLogs: args[1] == bool.TrueString);

Imports.RegisterOnMessage(async e =>
{
    try
    {
        var data = e.GetPropertyAsString("data") ?? string.Empty;
        var incoming = JsonSerializer.Deserialize<WorkerInputMessage>(data);
        var executor = services.GetRequiredService<WorkerInputMessage.IExecutor>();
        PostMessage(await incoming!.HandleAndGetOutputAsync(executor));
    }
    catch (Exception ex)
    {
        PostMessage(new WorkerOutputMessage.Failure(ex) { Id = -1 });
    }
});

PostMessage(new WorkerOutputMessage.Ready { Id = -1 });

// Keep running.
while (true)
{
    await Task.Delay(100);
}

static void PostMessage(WorkerOutputMessage message)
{
    Imports.PostMessage(JsonSerializer.Serialize(message));
}

[SupportedOSPlatform("browser")]
partial class Program;
