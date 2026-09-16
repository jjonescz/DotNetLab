#!/usr/bin/env dotnet

using System.Text.Json.Nodes;

if (args is not [{ Length: > 0 } arg])
{
    Console.Error.WriteLine("Usage: check-publish-output.cs <path to publish output directory>");
    return 1;
}

if (!Directory.Exists(arg))
{
    Console.Error.WriteLine($"Error: Directory '{arg}' does not exist.");
    return 1;
}

var serviceWorkerAssetsFile = Path.Join(arg, "wwwroot", "service-worker-assets.js");
if (!File.Exists(serviceWorkerAssetsFile))
{
    Console.Error.WriteLine($"Error: Service worker assets file not found at '{serviceWorkerAssetsFile}'.");
    return 1;
}

var serviceWorkerAssetsContent = File.ReadAllText(serviceWorkerAssetsFile);
const string prefix = "self.assetsManifest = ";
if (!serviceWorkerAssetsContent.StartsWith(prefix, StringComparison.Ordinal))
{
    Console.Error.WriteLine($"Error: Unexpected content in service worker assets file. Expected it to start with '{prefix}'.");
    return 1;
}

var serviceWorkerAssetsJson = serviceWorkerAssetsContent[prefix.Length..].TrimEnd(';', ' ', '\r', '\n');
var serviceWorkerAssets = JsonNode.Parse(serviceWorkerAssetsJson)!["assets"]!.AsArray();
if (serviceWorkerAssets.SingleOrDefault(a => a!["url"]!.GetValue<string>() == "index.html") == null)
{
    Console.Error.WriteLine("Error: index.html is not listed in the service worker assets manifest.");
    return 1;
}

if (serviceWorkerAssets.Any(a => a!["url"]!.GetValue<string>().StartsWith("worker/", StringComparison.Ordinal)))
{
    Console.Error.WriteLine("Error: Worker files must not be listed in the service worker assets manifest.");
    return 1;
}

var workerMainFile = Path.Join(arg, "wwwroot", "worker", "main.js");
if (!File.Exists(workerMainFile))
{
    Console.Error.WriteLine($"Error: Worker entry point not found at '{workerMainFile}'.");
    return 1;
}

var workerFrameworkDirectory = Path.Join(arg, "wwwroot", "worker", "_framework");
if (!Directory.EnumerateFiles(workerFrameworkDirectory, "DotNetLab.WorkerWebAssembly.*.wasm").Any())
{
    Console.Error.WriteLine($"Error: Worker WebAssembly file not found in '{workerFrameworkDirectory}'.");
    return 1;
}

var appFrameworkDirectory = Path.Join(arg, "wwwroot", "_framework");
string[] workerImplementationPatterns =
[
    "DotNetLab.Compiler.*.wasm",
    "DotNetLab.Worker.*.wasm",
    "DotNetLab.WorkerWebAssembly.*.wasm",
];
if (workerImplementationPatterns.Any(pattern => Directory.EnumerateFiles(appFrameworkDirectory, pattern).Any()))
{
    Console.Error.WriteLine($"Error: Worker implementation assemblies must not be present in '{appFrameworkDirectory}'.");
    return 1;
}

Console.Error.WriteLine("OK: index.html is listed in the service worker assets manifest.");
Console.Error.WriteLine("OK: worker bundle is not precached.");
Console.Error.WriteLine("OK: worker bundle is present.");
Console.Error.WriteLine("OK: worker implementation assemblies are absent from the app bundle.");
return 0;
