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
if (JsonNode.Parse(serviceWorkerAssetsJson)!["assets"]!.AsArray().SingleOrDefault(a => a!["url"]!.GetValue<string>() == "index.html") == null)
{
    Console.Error.WriteLine("Error: index.html is not listed in the service worker assets manifest.");
    return 1;
}

Console.Error.WriteLine("OK: index.html is listed in the service worker assets manifest.");
return 0;
