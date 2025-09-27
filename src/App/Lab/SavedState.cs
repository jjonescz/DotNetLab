using ProtoBuf;
using System.Collections.Frozen;

namespace DotNetLab.Lab;

static class WellKnownSlugs
{
    public static readonly FrozenDictionary<string, SavedState> ShorthandToState;
    public static readonly Dictionary<string, string> ShorthandToTitle;
    public static readonly FrozenDictionary<string, string> FullSlugToShorthand;

    static WellKnownSlugs()
    {
        IEnumerable<(string Shorthand, string Title, SavedState State)> entries =
        [
            ("csharp", "C#", SavedState.CSharp),
            ("razor", "Razor", SavedState.Razor),
            ("cshtml", "CSHTML", SavedState.Cshtml),
        ];

        ShorthandToState = entries.Select(t => KeyValuePair.Create(t.Shorthand, t.State)).ToFrozenDictionary();
        ShorthandToTitle = entries.Select(t => KeyValuePair.Create(t.Shorthand, t.Title)).ToDictionary();
        FullSlugToShorthand = entries
            .Select(static t => KeyValuePair.Create(Compressor.Compress(t.State), t.Shorthand))
            .ToFrozenDictionary();
    }
}

partial class Page
{
    private SavedState savedState = SavedState.Initial;
    private string? currentSlug;

    [MemberNotNull(nameof(currentSlug))]
    private void RefreshCurrentSlug()
    {
        var uri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
        currentSlug = uri.Fragment.TrimStart('#');
    }

    private async Task LoadStateFromUrlAsync()
    {
        if (currentSlug is null)
        {
            RefreshCurrentSlug();
        }

        var slug = currentSlug;

        var state = slug switch
        {
            _ when string.IsNullOrWhiteSpace(slug) => SavedState.Initial.WithPreferences(await loadPreferencesAsync()),
            _ when WellKnownSlugs.ShorthandToState.TryGetValue(slug, out var wellKnownState) => wellKnownState,
            _ => Compressor.Uncompress(slug),
        };

        // Sanitize the state to avoid crashes if possible
        // (anything can be in the user-provided URL hash,
        // but our app expects some invariants, like non-default ImmutableArrays).
        if (state.Inputs.IsDefault)
        {
            state = state with { Inputs = [] };
        }

        savedState = state;

        // Dispose old inputs.
        foreach (var input in inputs)
        {
            await input.DisposeAsync();
        }

        inputs.Clear();

        // Load inputs.
        activeInputTabId = IndexToInputTabId(0);
        var activeIndex = savedState.SelectedInputIndex;
        Input? firstInput = null;
        Input? activeInput = null;
        foreach (var (index, input) in savedState.Inputs.Index())
        {
            var model = await CreateModelAsync(input);
            Input inputModel = new(input.FileName, model) { NewContent = input.Text };
            inputs.Add(inputModel);

            if (index == 0)
            {
                firstInput = inputModel;
            }

            if (index == activeIndex)
            {
                activeInput = inputModel;
            }
        }

        if (savedState.Configuration is { } savedConfiguration)
        {
            var input = InitialCode.Configuration.ToInputCode() with { Text = savedConfiguration };
            configuration = new(input.FileName, await CreateModelAsync(input)) { NewContent = input.Text };
        }
        else
        {
            configuration = null;
        }

        {
            // Unset cache info (the loaded state might not be cached).
            if (compiled is { Input: var input, Output: var output, CacheInfo: { } })
            {
                var timestamp = DateTimeOffset.Now;
                compiled = (input, output, CacheInfo: null, Start: timestamp, End: timestamp);
            }
        }

        activeInputTabId = IndexToInputTabId(activeIndex);
        selectedOutputType = savedState.SelectedOutputType;

        await OnWorkspaceChangedAsync();

        if ((activeInput ?? firstInput) is { } selectInput)
        {
            currentInput = selectInput;
            await inputEditor.SetModel(selectInput.Model);
        }

        // Try loading from cache.
        if (!await TryLoadFromTemplateCacheAsync(state, updateOutput: false) &&
            settings.EnableCaching)
        {
            _ = TryLoadFromCacheAsync(state, updateOutput: true);
        }
        else
        {
            await UpdateOutputDisplayAsync(
                updateTimestampMode: OutputActionMode.Never,
                storeInCacheMode: OutputActionMode.Never);
        }

        // Load settings.
        await settings.LoadFromStateAsync(savedState);

        async Task<CompilationPreferences> loadPreferencesAsync()
        {
            return await LocalStorage.ContainKeyAsync(nameof(CompilationPreferences))
                ? (await LocalStorage.GetItemAsync<CompilationPreferences>(nameof(CompilationPreferences)) ?? CompilationPreferences.Default)
                : CompilationPreferences.Default;
        }
    }

    private bool NavigateToSlug(string slug)
    {
        if (slug != currentSlug)
        {
            NavigationManager.NavigateTo(NavigationManager.BaseUri + "#" + slug, forceLoad: false);
            currentSlug = slug;
            return true;
        }

        return false;
    }

    internal async Task<SavedState> SaveStateToUrlAsync(Func<SavedState, SavedState>? updater = null, bool savePreferences = false)
    {
        // Always save the current editor texts.
        var inputsToSave = await getInputsAsync();
        var configurationToSave = configuration is null ? null : await configuration.Model.GetTextAsync();

        using (Util.EnsureSync())
        {
            savedState = savedState with
            {
                Inputs = inputsToSave,
                Configuration = configurationToSave,
            };

            if (updater != null)
            {
                savedState = updater(savedState);
            }
        }

        if (savePreferences)
        {
            var preferences = savedState.GetPreferences();
            await LocalStorage.SetItemAsync(nameof(CompilationPreferences), preferences);
        }

        var newSlug = Compressor.Compress(savedState);

        if (WellKnownSlugs.FullSlugToShorthand.TryGetValue(newSlug, out var wellKnownSlug))
        {
            newSlug = wellKnownSlug;
        }

        NavigateToSlug(newSlug);

        return savedState;

        async Task<ImmutableArray<InputCode>> getInputsAsync()
        {
            var inputsSnapshot = inputs.ToArray(); // to avoid modifications during enumeration
            var builder = ImmutableArray.CreateBuilder<InputCode>(inputsSnapshot.Length);
            foreach (var (fileName, model) in inputsSnapshot)
            {
                var text = await model.GetTextAsync();
                builder.Add(new() { FileName = fileName, Text = text });
            }
            return builder.ToImmutable();
        }
    }
}

/// <remarks>
/// <para>
/// This is currently not comparable because <see cref="Inputs"/> is an <see cref="ImmutableArray{T}"/>.
/// We would need to change it to <see cref="Sequence{T}"/> but also ensure that it still results in the same ProtoBuf encoding.
/// </para>
/// </remarks>
[ProtoContract]
internal sealed record SavedState
{
    private static readonly SavedState defaults = new()
    {
        RazorToolchain = RazorToolchain.SourceGeneratorOrInternalApi,
    };

    public static SavedState Initial => CSharp;

    public static SavedState CSharp { get; } = defaults with
    {
        Inputs = [InitialCode.CSharp.ToInputCode()],
        SelectedOutputType = "run",
    };

    public static SavedState Razor { get; } = defaults with
    {
        Inputs = [InitialCode.Razor.ToInputCode(), InitialCode.RazorImports.ToInputCode()],
        SelectedOutputType = "gcs",
    };

    public static SavedState Cshtml { get; } = defaults with
    {
        Inputs = [InitialCode.Cshtml.ToInputCode()],
        SelectedOutputType = "gcs",
    };

    [ProtoMember(1)]
    public ImmutableArray<InputCode> Inputs { get; init; }

    [ProtoMember(8)]
    public int SelectedInputIndex { get; init; }

    [ProtoMember(9)]
    public string? SelectedOutputType { get; init; }

    [ProtoMember(10)]
    [Obsolete($"Use {nameof(RazorStrategy)} instead", error: true)]
    public string? GenerationStrategy
    {
        get => RazorStrategy == RazorStrategy.DesignTime ? "designTime" : null;
        init => RazorStrategy = value == "designTime" ? RazorStrategy.DesignTime : RazorStrategy.Runtime;
    }

    [ProtoMember(5)]
    public string? Configuration { get; init; }

    [ProtoMember(12)]
    public RazorToolchain RazorToolchain { get; init; }

    public RazorStrategy RazorStrategy { get; init; }

    [ProtoMember(13)]
    public bool DecodeCustomAttributeBlobs { get; init; }

    [ProtoMember(4)]
    public string? SdkVersion { get; init; }

    [ProtoMember(2)]
    public string? RoslynVersion { get; init; }

    [ProtoMember(6)]
    public BuildConfiguration RoslynConfiguration { get; init; }

    [ProtoMember(3)]
    public string? RazorVersion { get; init; }

    [ProtoMember(7)]
    public BuildConfiguration RazorConfiguration { get; init; }

    [ProtoMember(11)]
    public DateTime? Timestamp { get; init; }

    public bool HasDefaultCompilerConfiguration
    {
        get
        {
            return string.IsNullOrEmpty(RoslynVersion) &&
                string.IsNullOrEmpty(RazorVersion) &&
                string.IsNullOrEmpty(Configuration);
        }
    }

    /// <summary>
    /// Trims down to a state that is important for compilation output.
    /// Used as cache key in <see cref="InputOutputCache"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Timestamp"/> is included as well, albeit not important for compilation per se,
    /// it is used to allow different users having the same input and get a unique cache key
    /// (because the cache does not allow overwriting cache entries
    /// to prevent anyone changing already shared snippet outputs).
    /// </remarks>
    public SavedState ToCacheKey()
    {
        return this with
        {
            SelectedInputIndex = 0,
            SelectedOutputType = null,
            SdkVersion = null,
        };
    }

    public string ToCacheSlug()
    {
        return Compressor.Compress(ToCacheKey());
    }

    public CompilationInput ToCompilationInput()
    {
        return new(Inputs)
        {
            Configuration = Configuration,
            RazorToolchain = RazorToolchain,
            RazorStrategy = RazorStrategy,
            Preferences = GetPreferences(),
        };
    }

    public CompilationPreferences GetPreferences()
    {
        return new()
        {
            DecodeCustomAttributeBlobs = DecodeCustomAttributeBlobs,
        };
    }

    public SavedState WithPreferences(CompilationPreferences preferences)
    {
        return this with
        {
            DecodeCustomAttributeBlobs = preferences.DecodeCustomAttributeBlobs,
        };
    }

    public static SavedState From(CompilationInput input)
    {
        return new SavedState()
        {
            Inputs = input.Inputs,
            Configuration = input.Configuration,
            RazorToolchain = input.RazorToolchain,
            RazorStrategy = input.RazorStrategy,
        }
        .WithPreferences(input.Preferences);
    }
}
