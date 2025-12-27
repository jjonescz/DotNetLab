using AwesomeAssertions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DotNetLab;

[TestClass]
public sealed class JsonTests
{
    public required TestContext TestContext { get; set; }

    /// <summary>
    /// Since we are skipping default values when serializing,
    /// we cannot have required properties as they would fail
    /// during deserialization when default (i.e., missing).
    /// </summary>
    [TestMethod]
    public void WorkerJsonContext_RequiredProperties()
    {
        var context = WorkerJsonContext.Default;

        Assert.AreEqual(JsonIgnoreCondition.WhenWritingDefault, context.Options.DefaultIgnoreCondition);

        var requiredProperties = context.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(prop => prop.GetValue(context))
            .OfType<JsonTypeInfo>()
            .SelectMany(typeInfo => typeInfo.Properties)
            .Where(propInfo => propInfo.IsRequired)
            .Select(propInfo => $"{propInfo.DeclaringType.FullName}.{propInfo.Name}")
            .ToImmutableArray();

        foreach (var requiredProperty in requiredProperties)
        {
            TestContext.WriteLine(requiredProperty);
        }

        requiredProperties.Should().BeEmpty();
    }

    /// <inheritdoc cref="WorkerJsonContext_RequiredProperties"/>
    [TestMethod]
    public void WorkerJsonContext_RequiredProperties_Roundtrip()
    {
        var message = new WorkerInputMessage.Ping { Id = 0 };
        var serialized = JsonSerializer.Serialize(message, WorkerJsonContext.Default.WorkerInputMessage);
        var deserialized = JsonSerializer.Deserialize(serialized, WorkerJsonContext.Default.WorkerInputMessage);
        Assert.AreEqual(message, deserialized);
    }
}
