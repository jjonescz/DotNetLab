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
    [TestMethod, DynamicData(nameof(GetContextsIgnoringDefaultValues))]
    public void ContextsIgnoringDefaultValues_RequiredProperties(JsonSerializerContext context)
    {
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

    private static IEnumerable<object[]> GetContextsIgnoringDefaultValues()
    {
        var result = enumerate().ToArray();
        Assert.HasCount(2, result);
        return result;

        static IEnumerable<object[]> enumerate()
        {
            var seenTypes = new HashSet<Type>();
            var seenAssemblies = new HashSet<string>();
            var queue = new Queue<Assembly>(1);
            queue.Enqueue(typeof(JsonTests).Assembly);
            while (queue.TryDequeue(out var assembly))
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (!type.IsSubclassOf(typeof(JsonSerializerContext)) ||
                        !seenTypes.Add(type) ||
                        type.GetCustomAttributes<ObsoleteAttribute>().Any())
                    {
                        continue;
                    }

                    var context = (JsonSerializerContext)type
                        .GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!
                        .GetValue(null)!;

                    if (context.Options.DefaultIgnoreCondition != JsonIgnoreCondition.WhenWritingDefault)
                    {
                        continue;
                    }

                    yield return [context];
                }

                foreach (var referenced in assembly.GetReferencedAssemblies())
                {
                    if (referenced.Name?.StartsWith("DotNetLab.") != true)
                    {
                        continue;
                    }

                    if (seenAssemblies.Add(referenced.ToString()))
                    {
                        queue.Enqueue(Assembly.Load(referenced));
                    }
                }
            }
        }
    }

    /// <summary>
    /// See <see cref="ContextsIgnoringDefaultValues_RequiredProperties"/>.
    /// This tests roundtrip of one message to ensure the serialization can handle required properties.
    /// </summary>
    [TestMethod]
    public void WorkerJsonContext_RequiredProperties_Roundtrip()
    {
        var context = WorkerJsonContext.Default;
        Assert.AreEqual(JsonIgnoreCondition.WhenWritingDefault, context.Options.DefaultIgnoreCondition);

        var message = new WorkerInputMessage.Ping { Id = 0 };
        var serialized = JsonSerializer.Serialize(message, context.WorkerInputMessage);
        var deserialized = JsonSerializer.Deserialize(serialized, context.WorkerInputMessage);
        Assert.AreEqual(message, deserialized);
    }
}
