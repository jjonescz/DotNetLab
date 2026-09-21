using AwesomeAssertions;
using DotNetLab.Features.Preferences;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace DotNetLab;

[TestClass]
public sealed class SettingsStoreTests
{
    [TestMethod]
    public async Task LoadAsync_PassesKeysAsASingleArgument()
    {
        var js = new RecordingJsRuntime();
        var store = new SettingsStore(js, NullLogger<SettingsStore>.Instance);

        var snapshot = await store.LoadAsync();

        js.Identifier.Should().Be("netLabPrefs.readSettings");
        js.Args.Should().ContainSingle()
            .Which.Should().BeSameAs(SettingsStorageSchema.Keys);
        snapshot.Should().NotBeNull();
        snapshot!.WordWrap.Should().BeTrue();
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public string? Identifier { get; private set; }
        public object?[]? Args { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Identifier = identifier;
            Args = args;
            if (typeof(TValue) == typeof(Dictionary<string, string?>))
            {
                var values = new Dictionary<string, string?>
                {
                    [SettingsStorageSchema.WordWrapKey] = "true",
                };
                return (ValueTask<TValue>)(object)new ValueTask<Dictionary<string, string?>>(values);
            }

            return default;
        }
    }
}
