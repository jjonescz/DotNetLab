using AwesomeAssertions;
using DotNetLab.Infrastructure.Caching.Compilation;
using DotNetLab.Lab;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace DotNetLab;

[TestClass]
public sealed class CompilationCacheTests
{
    [TestMethod]
    public async Task Get_PrefersLocalAndDoesNotHitRemote()
    {
        var compiled = CompiledAssembly.Fail("local");
        var local = new FakeStore
        {
            Items =
            {
                [CompilationCacheKey.Create(SavedState.CSharp)] = new CachedCompilation(compiled, DateTimeOffset.UnixEpoch),
            },
        };
        var remote = new FakeStore();
        var cache = new CompilationCache(local, remote);

        var result = await cache.GetAsync(SavedState.CSharp);
        result.Should().NotBeNull();
        result.Value.Output.GetGlobalOutput("fail")!.Text.Should().Be("local");
        local.Gets.Should().Be(1);
        remote.Gets.Should().Be(0);
    }

    [TestMethod]
    public async Task Get_RemoteHitFillsLocal()
    {
        var compiled = CompiledAssembly.Fail("remote");
        var local = new FakeStore();
        var remote = new FakeStore
        {
            Items =
            {
                [CompilationCacheKey.Create(SavedState.CSharp)] = new CachedCompilation(compiled, DateTimeOffset.UnixEpoch),
            },
        };
        var cache = new CompilationCache(local, remote);

        var result = await cache.GetAsync(SavedState.CSharp);
        result.Should().NotBeNull();
        result.Value.Output.GetGlobalOutput("fail")!.Text.Should().Be("remote");
        local.Stores.Should().Be(1);
        local.Items.Should().ContainKey(CompilationCacheKey.Create(SavedState.CSharp));
    }

    [TestMethod]
    public async Task Get_MissDoesNotWriteLocal()
    {
        var local = new FakeStore();
        var remote = new FakeStore();
        var cache = new CompilationCache(local, remote);

        var result = await cache.GetAsync(SavedState.CSharp);
        result.Should().BeNull();
        local.Stores.Should().Be(0);
        remote.Stores.Should().Be(0);
    }

    [TestMethod]
    public async Task Get_CoalescesConcurrentLookups()
    {
        var compiled = CompiledAssembly.Fail("once");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var local = new FakeStore { BlockGet = gate };
        var remote = new FakeStore
        {
            Items =
            {
                [CompilationCacheKey.Create(SavedState.CSharp)] = new CachedCompilation(compiled, DateTimeOffset.UnixEpoch),
            },
        };
        var cache = new CompilationCache(local, remote);

        var first = cache.GetAsync(SavedState.CSharp);
        var second = cache.GetAsync(SavedState.CSharp);
        await Task.Delay(50);
        local.Gets.Should().Be(1);
        gate.SetResult();

        var results = await Task.WhenAll(first.AsTask(), second.AsTask());
        results.Should().AllSatisfy(item => item!.Value.Output.GetGlobalOutput("fail")!.Text.Should().Be("once"));
        local.Gets.Should().Be(1);
        remote.Gets.Should().Be(1);
    }

    [TestMethod]
    public async Task Store_WritesLocalAndRemote()
    {
        var local = new FakeStore();
        var remote = new FakeStore();
        var cache = new CompilationCache(local, remote);

        await cache.StoreAsync(SavedState.CSharp, CompiledAssembly.Fail("saved"));
        local.Stores.Should().Be(1);
        remote.Stores.Should().Be(1);
        local.Items.Should().ContainKey(CompilationCacheKey.Create(SavedState.CSharp));
        remote.Items.Should().ContainKey(CompilationCacheKey.Create(SavedState.CSharp));
    }

    [TestMethod]
    public async Task IndexedDb_GetTreatsMissingJsAsMiss()
    {
        var cache = new IndexedDbCompilationCache(new MissingJsRuntime(), NullLogger<IndexedDbCompilationCache>.Instance);
        var result = await cache.GetAsync("abc", CancellationToken.None);
        result.Should().BeNull();
    }

    [TestMethod]
    public void CacheKey_IsStableForTheSameState()
    {
        CompilationCacheKey.Create(SavedState.CSharp).Should().Be(CompilationCacheKey.Create(SavedState.CSharp));
        CompilationCacheKey.Create(SavedState.CSharp).Should().NotBe(CompilationCacheKey.Create(SavedState.Razor));
    }

    [TestMethod]
    public void CacheKey_IncludesSchemaPrefix()
    {
        var key = CompilationCacheKey.Create(SavedState.CSharp);
        var prefix = $"v{CompilationCacheKey.Schema}-";
        key.Should().StartWith(prefix);
        key.Length.Should().Be(prefix.Length + 32);
        key.Should().NotBe(key[prefix.Length..]);
    }

    private sealed class FakeStore : ICompilationCacheStore
    {
        public Dictionary<string, CachedCompilation> Items { get; } = new(StringComparer.Ordinal);

        public int Gets { get; private set; }

        public int Stores { get; private set; }

        public TaskCompletionSource? BlockGet { get; init; }

        public async ValueTask<CachedCompilation?> GetAsync(string key, CancellationToken cancellationToken)
        {
            Gets++;
            if (BlockGet is not null)
            {
                await BlockGet.Task.WaitAsync(cancellationToken);
            }

            return Items.TryGetValue(key, out var value) ? value : null;
        }

        public ValueTask StoreAsync(string key, CachedCompilation value, CancellationToken cancellationToken)
        {
            Stores++;
            Items[key] = value;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MissingJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => throw new JSException("no js");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => throw new JSException("no js");
    }
}
