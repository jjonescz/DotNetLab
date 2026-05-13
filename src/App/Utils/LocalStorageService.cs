using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace DotNetLab;

internal interface ILocalStorageService
{
    ValueTask<bool> ContainKeyAsync(string key, CancellationToken cancellationToken = default);

    ValueTask<T?> GetItemAsync<T>(string key, CancellationToken cancellationToken = default);

    ValueTask SetItemAsync<T>(string key, T value, CancellationToken cancellationToken = default);
}

/// <summary>
/// From <see href="https://github.com/Blazored/LocalStorage"/> version 4.5.0.
/// </summary>
internal sealed class LocalStorageService(IJSRuntime jsRuntime) : ILocalStorageService
{
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async ValueTask<bool> ContainKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        return await jsRuntime.InvokeAsync<bool>("localStorage.hasOwnProperty", cancellationToken, key);
    }

    public async ValueTask<T?> GetItemAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var json = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", cancellationToken, key);
        return string.IsNullOrWhiteSpace(json)
            ? default
            : JsonSerializer.Deserialize<T>(json, jsonOptions);
    }

    public async ValueTask SetItemAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var json = JsonSerializer.Serialize(value, jsonOptions);
        await jsRuntime.InvokeVoidAsync("localStorage.setItem", cancellationToken, key, json);
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentNullException(nameof(key));
        }
    }
}
