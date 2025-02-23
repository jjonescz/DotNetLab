using BlazorMonaco.Editor;
using ProtoBuf;
using System.Runtime.CompilerServices;

namespace DotNetLab.Lab;

partial class Page
{
    private SavedState savedState = SavedState.Initial;

    private string GetCurrentSlug()
    {
        var uri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
        return uri.Fragment.TrimStart('#');
    }

    private async Task<(SavedState State, string Slug)?> LoadStateFromUrlAsync()
    {
        var slug = GetCurrentSlug();

        var (loadedState, uncompressed) = slug switch
        {
            _ when string.IsNullOrWhiteSpace(slug) => (SavedState.Initial, false),
            "csharp" => (InitialCode.CSharp.ToSavedState(), false),
            "razor" => (SavedState.Initial, false),
            "cshtml" => (InitialCode.Cshtml.ToSavedState(), false),
            _ => (Compressor.Uncompress(slug), true),
        };
        savedState = loadedState;

        // Load inputs.
        inputs.Clear();
        activeInputTabId = IndexToInputTabId(0);
        var activeIndex = savedState.SelectedInputIndex;
        TextModel? firstModel = null;
        TextModel? activeModel = null;
        foreach (var (index, input) in savedState.Inputs.Index())
        {
            var model = await CreateModelAsync(input);
            inputs.Add(new(input.FileName, model) { NewContent = input.Text });

            if (index == 0)
            {
                firstModel = model;
            }

            if (index == activeIndex)
            {
                activeModel = model;
            }
        }

        if (savedState.Configuration is { } savedConfiguration)
        {
            var input = InitialCode.Configuration.ToInputCode() with { Text = savedConfiguration };
            configuration = new(input.FileName, await CreateModelAsync(input));
        }

        activeInputTabId = IndexToInputTabId(activeIndex);
        selectedOutputType = savedState.SelectedOutputType;
        generationStrategy = savedState.GenerationStrategy;

        OnWorkspaceChanged();

        if ((activeModel ?? firstModel) is { } selectModel)
        {
            await inputEditor.SetModel(selectModel);
        }

        // Load settings.
        await settings.LoadFromStateAsync(savedState);

        return uncompressed ? (loadedState, slug) : null;
    }

    internal async Task<(SavedState State, string Slug)> SaveStateToUrlAsync(Func<SavedState, SavedState>? updater = null, [CallerMemberName] string caller = "")
    {
        // Always save the current editor texts.
        var inputsToSave = await getInputsAsync();
        var configurationToSave = configuration is null ? null : await getInputAsync(configuration.Model);

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

        var newSlug = Compressor.Compress(savedState);
        if (newSlug != GetCurrentSlug())
        {
            NavigationManager.NavigateTo(NavigationManager.BaseUri + "#" + newSlug, forceLoad: false);
        }

        return (savedState, newSlug);

        async Task<ImmutableArray<InputCode>> getInputsAsync()
        {
            var inputsSnapshot = inputs.ToArray(); // to avoid modifications during enumeration
            var builder = ImmutableArray.CreateBuilder<InputCode>(inputsSnapshot.Length);
            foreach (var (fileName, model) in inputsSnapshot)
            {
                var text = await getInputAsync(model);
                builder.Add(new() { FileName = fileName, Text = text });
            }
            return builder.ToImmutable();
        }

        static async Task<string> getInputAsync(TextModel model)
        {
            return await model.GetValue(EndOfLinePreference.TextDefined, preserveBOM: true);
        }
    }
}

[ProtoContract]
internal sealed record SavedState
{
    public static SavedState Initial { get; } = new()
    {
        Inputs = [InitialCode.Razor.ToInputCode(), InitialCode.RazorImports.ToInputCode()],
    };

    [ProtoMember(1)]
    public ImmutableArray<InputCode> Inputs { get; init; }

    [ProtoMember(8)]
    public int SelectedInputIndex { get; init; }

    [ProtoMember(9)]
    public string? SelectedOutputType { get; init; }

    [ProtoMember(10)]
    public string? GenerationStrategy { get; init; }

    [ProtoMember(5)]
    public string? Configuration { get; init; }

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

    public CompilationInput ToCompilationInput()
    {
        return new(Inputs)
        {
            Configuration = Configuration,
        };
    }
}
