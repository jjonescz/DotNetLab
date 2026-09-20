using System.Text;
using System.Text.Json;

namespace DotNetLab.Features.Outputs;

public sealed partial class OutputWorkspace
{
    public string SerializeOutputTabs()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteSavedKind(writer, "cs", OutputFileKind.Cs);
            WriteSavedKind(writer, "razor", OutputFileKind.Razor);
            WriteSavedKind(writer, "cshtml", OutputFileKind.Cshtml);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void ApplySavedOutputTabs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            ApplySavedKind(document.RootElement, "cs", OutputFileKind.Cs);
            ApplySavedKind(document.RootElement, "razor", OutputFileKind.Razor);
            ApplySavedKind(document.RootElement, "cshtml", OutputFileKind.Cshtml);
        }
        catch (JsonException)
        {
            return;
        }

        _openOutputTabs.Clear();
        Revision++;
        EnsureActiveOutput();
        Notify();
    }

    private void WriteSavedKind(Utf8JsonWriter writer, string property, OutputFileKind kind)
    {
        writer.WritePropertyName(property);
        writer.WriteStartObject();
        writer.WriteStartArray("order");
        foreach (var id in OutputTabOrder(kind))
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("hidden");
        foreach (var id in HiddenOutputTabs(kind))
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private void ApplySavedKind(JsonElement root, string property, OutputFileKind kind)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            ResetKindToDefault(kind);
            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            ApplyLegacyVisibleIds(kind, ReadStringArray(value));
            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            ResetKindToDefault(kind);
            return;
        }

        var catalog = OutputCatalog.For(kind).Select(output => output.Id).ToHashSet(StringComparer.Ordinal);
        var order = value.TryGetProperty("order", out var orderElement) && orderElement.ValueKind == JsonValueKind.Array
            ? SanitizeOrder(kind, ReadStringArray(orderElement))
            : OutputCatalog.DefaultOrder(kind);
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (value.TryGetProperty("hidden", out var hiddenElement) && hiddenElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in ReadStringArray(hiddenElement))
            {
                if (catalog.Contains(id) && id != OutputCatalog.ErrorsId)
                {
                    hidden.Add(id);
                }
            }
        }

        _outputTabOrder[kind] = order;
        _hiddenOutputTabs[kind] = hidden;
    }

    private void ApplyLegacyVisibleIds(OutputFileKind kind, IReadOnlyList<string> visible)
    {
        var catalog = OutputCatalog.For(kind);
        var catalogIds = catalog.Select(output => output.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        var hidden = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in visible)
        {
            if (catalogIds.Contains(id) && seen.Add(id))
            {
                order.Add(id);
            }
        }

        foreach (var output in catalog)
        {
            if (seen.Add(output.Id))
            {
                order.Add(output.Id);
                if (output.Id != OutputCatalog.ErrorsId)
                {
                    hidden.Add(output.Id);
                }
            }
        }

        if (!order.Contains(OutputCatalog.ErrorsId))
        {
            order.Add(OutputCatalog.ErrorsId);
        }

        hidden.Remove(OutputCatalog.ErrorsId);
        _outputTabOrder[kind] = order;
        _hiddenOutputTabs[kind] = hidden;
    }

    private static List<string> SanitizeOrder(OutputFileKind kind, IReadOnlyList<string> ids)
    {
        var catalog = OutputCatalog.For(kind);
        var catalogIds = catalog.Select(output => output.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var id in ids)
        {
            if (catalogIds.Contains(id) && seen.Add(id))
            {
                order.Add(id);
            }
        }

        foreach (var output in catalog)
        {
            if (seen.Add(output.Id))
            {
                order.Add(output.Id);
            }
        }

        return order;
    }

    private static List<string> ReadStringArray(JsonElement value)
    {
        var ids = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = item.GetString();
            if (!string.IsNullOrEmpty(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private void ResetKindToDefault(OutputFileKind kind)
    {
        _outputTabOrder[kind] = OutputCatalog.DefaultOrder(kind);
        _hiddenOutputTabs[kind] = new HashSet<string>(StringComparer.Ordinal);
    }
}
