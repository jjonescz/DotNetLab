#!/usr/bin/env dotnet

using System.Text.Json.Nodes;
using System.Security.Cryptography;

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
var assets = JsonNode.Parse(serviceWorkerAssetsJson)!["assets"]!.AsArray();
if (assets.SingleOrDefault(a => a!["url"]!.GetValue<string>() == "index.html") == null)
{
    Console.Error.WriteLine("Error: index.html is not listed in the service worker assets manifest.");
    return 1;
}

var webRoot = Path.Join(arg, "wwwroot");
foreach (var asset in assets)
{
    var relativePath = asset!["url"]!.GetValue<string>();
    if (!relativePath.EndsWith(".wasm", StringComparison.Ordinal))
    {
        continue;
    }

    var path = Path.Join(webRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    if (File.Exists(path))
    {
        Console.Error.WriteLine($"Error: Raw WASM asset '{relativePath}' should not be deployed.");
        return 1;
    }

    if (!File.Exists(path + ".gz"))
    {
        Console.Error.WriteLine($"Error: Gzip-compressed WASM asset '{relativePath}.gz' was not found.");
        return 1;
    }

    asset!["compressedHash"] = $"sha256-{Convert.ToBase64String(
        SHA256.HashData(File.ReadAllBytes(path + ".gz")))}";
}

File.WriteAllText(serviceWorkerAssetsFile, $"{prefix}{assets.Root}\n;");
Console.Error.WriteLine("OK: publish output is ready for Cloudflare Pages.");
return 0;
