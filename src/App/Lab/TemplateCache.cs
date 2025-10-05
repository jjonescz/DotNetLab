using System.Collections.Concurrent;
using System.Text.Json;

namespace DotNetLab.Lab;

/// <summary>
/// Caches outputs of a pre-defined set of templates so they are faster to load
/// (no need to wait for the initial compilation which can be slow).
/// </summary>
internal sealed partial class TemplateCache
{
    private readonly ConcurrentDictionary<CompilationInput, CompiledAssembly> map = new();

    public readonly ImmutableArray<(string Name, CompilationInput Input, Func<ReadOnlySpan<byte>> Json)> Entries =
    [
        (nameof(SavedState.CSharp), SavedState.CSharp.ToCompilationInput(), GetCSharp),
        (nameof(SavedState.Razor), SavedState.Razor.ToCompilationInput(), GetRazor),
        (nameof(SavedState.Cshtml), SavedState.Cshtml.ToCompilationInput(), GetCshtml),
    ];

    public bool HasInput(SavedState state)
    {
        return TryGetKey(state, out var input) &&
            (map.ContainsKey(input) || TryFindEntry(input, out _));
    }

    public bool TryGetOutput(
        SavedState state,
        [NotNullWhen(returnValue: true)] out CompilationInput? input,
        [NotNullWhen(returnValue: true)] out CompiledAssembly? output)
    {
        if (!TryGetKey(state, out input))
        {
            output = null;
            return false;
        }

        if (map.TryGetValue(input, out output))
        {
            return true;
        }

        if (!TryFindEntry(input, out var jsonFactory))
        {
            return false;
        }

        var json = jsonFactory();
        output = map.GetOrAdd(input, JsonSerializer.Deserialize(json, WorkerJsonContext.Default.CompiledAssembly)!);
        return true;
    }

    private static bool TryGetKey(SavedState state,
        [NotNullWhen(returnValue: true)] out CompilationInput? key)
    {
        if (!IsEligible(state))
        {
            key = null;
            return false;
        }

        key = state.ToCompilationInput();
        return true;
    }

    private static bool IsEligible(SavedState state)
    {
        return state.HasDefaultCompilerConfiguration;
    }

    private bool TryFindEntry(CompilationInput input,
        [NotNullWhen(returnValue: true)] out Func<ReadOnlySpan<byte>>? jsonFactory)
    {
        foreach (var entry in Entries)
        {
            if (entry.Input.Equals(input))
            {
                jsonFactory = entry.Json;
                return true;
            }
        }

        jsonFactory = null;
        return false;
    }
}
