﻿using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.Common;
using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetInternals.Lab;

internal sealed class AzDoDownloader
{
    private static readonly string baseAddress = "https://dev.azure.com/dnceng-public/public";
    private static readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter(),
            new TypeConverterJsonConverterFactory(),
        },
    };

    private readonly HttpClient client = new();

    public async Task<ImmutableArray<LoadedAssembly>> DownloadAsync(int pullRequestNumber)
    {
        var builds = await GetBuildsAsync(
            definitionId: 95, // roslyn-CI
            branchName: $"refs/pull/{pullRequestNumber}/merge",
            top: 1);

        if (builds is not { Count: > 0, Value: [{ } build, ..] })
        {
            throw new InvalidOperationException($"No builds of PR {pullRequestNumber} found.");
        }

        throw new InvalidOperationException($"Found build {build.BuildNumber}.");
    }

    private async Task<AzDoCollection<Build>?> GetBuildsAsync(int definitionId, string branchName, int top)
    {
        var uri = new UriBuilder(baseAddress);
        uri.AppendPathSegments("_apis", "build", "builds");
        uri.AppendQuery("definitions", definitionId.ToString(CultureInfo.InvariantCulture));
        uri.AppendQuery("branchName", branchName);
        uri.AppendQuery("$top", top.ToString());
        uri.AppendQuery("api-version", "7.1");

        return await client.GetFromJsonAsync<AzDoCollection<Build>>(uri.ToString(), options);
    }
}

internal sealed class AzDoCollection<T>
{
    public required int Count { get; init; }
    public required ImmutableArray<T> Value { get; init; }
}

/// <summary>
/// Can convert types that have a <see cref="TypeConverterAttribute"/>.
/// </summary>
internal sealed class TypeConverterJsonConverterFactory : JsonConverterFactory
{
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var typeConverter = TypeDescriptor.GetConverter(typeToConvert);
        var jsonConverter = (JsonConverter?)Activator.CreateInstance(
            typeof(TypeConverterJsonConverter<>).MakeGenericType([typeToConvert]),
            [typeConverter]);
        return jsonConverter;
    }

    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.GetCustomAttribute<TypeConverterAttribute>() != null;
    }
}

/// <summary>
/// Created by <see cref="TypeConverterJsonConverterFactory"/>.
/// </summary>
internal sealed class TypeConverterJsonConverter<T>(TypeConverter typeConverter) : JsonConverter<T>
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeConverter.CanConvertFrom(typeof(string)) || typeConverter.CanConvertTo(typeof(string));
    }

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetString() is { } s ? (T?)typeConverter.ConvertFromInvariantString(s) : default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is not null)
        {
            writer.WriteStringValue(typeConverter.ConvertToInvariantString(value));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
