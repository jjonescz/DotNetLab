using DotNetLab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;
using System.Text.Json;

Console.WriteLine("Worker started.");

if (args.Length != 2)
{
    Console.WriteLine($"Expected 2 args, got {args.Length}.");
    return;
}

var services = WorkerServices.Create(
    baseUrl: args[0],
    logLevel: Enum.Parse<LogLevel>(args[1]));

Imports.RegisterOnMessage(async e =>
{
    try
    {
        var data = e.GetPropertyAsString("data") ?? string.Empty;
        var incoming = JsonSerializer.Deserialize(data, WorkerJsonContext.Default.WorkerInputMessage);
        var executor = services.GetRequiredService<WorkerInputMessage.IExecutor>();
        PostMessage(await incoming!.HandleAndGetOutputAsync(executor));
    }
    catch (Exception ex)
    {
        PostMessage(new WorkerOutputMessage.Failure(ex) { Id = -1, InputType = WorkerOutputMessage.UnknownInputType });
    }
});

PostMessage(new WorkerOutputMessage.Ready { Id = -1, InputType = WorkerOutputMessage.NoInputType });

// Keep running.
while (true)
{
    await Task.Delay(100);
}

static void PostMessage(WorkerOutputMessage message)
{
    Imports.PostMessage(JsonSerializer.Serialize(message, WorkerJsonContext.Default.WorkerOutputMessage));
}

[SupportedOSPlatform("browser")]
partial class Program;
