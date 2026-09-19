using DotNetLab.Editor.Monaco;
using DotNetLab.Lab;
using Microsoft.JSInterop;

namespace DotNetLab.Editor.LanguageServices;

public sealed class LabCursorSync(BlazorMonacoInterop interop) : IAsyncDisposable
{
    private string? _sourceEditorId;
    private string? _outputEditorId;
    private DocumentMapping _inputToOutput;
    private DocumentMapping _outputToInput;
    private IAsyncDisposable? _sourceSubscription;
    private IAsyncDisposable? _outputSubscription;

    public void Enable(CompiledFileOutputMetadata? metadata)
    {
        if (metadata is { InputToOutput: { } inputToOutput, OutputToInput: { } outputToInput })
        {
            _inputToOutput = DocumentMapping.Deserialize(inputToOutput);
            _outputToInput = DocumentMapping.Deserialize(outputToInput);
        }
        else
        {
            _inputToOutput = default;
            _outputToInput = default;
        }
    }

    public async Task AttachSourceAsync(string editorId)
    {
        _sourceSubscription = await ReplaceSubscriptionAsync(_sourceSubscription, editorId, OnSourceCursorAsync);
        _sourceEditorId = editorId;
    }

    public async Task AttachOutputAsync(string editorId)
    {
        _outputSubscription = await ReplaceSubscriptionAsync(_outputSubscription, editorId, OnOutputCursorAsync);
        _outputEditorId = editorId;
    }

    public async Task DetachAsync(string editorId)
    {
        if (string.Equals(_sourceEditorId, editorId, StringComparison.Ordinal))
        {
            _sourceSubscription = await DisposeSubscriptionAsync(_sourceSubscription);
            _sourceEditorId = null;
        }

        if (string.Equals(_outputEditorId, editorId, StringComparison.Ordinal))
        {
            _outputSubscription = await DisposeSubscriptionAsync(_outputSubscription);
            _outputEditorId = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sourceSubscription = await DisposeSubscriptionAsync(_sourceSubscription);
        _outputSubscription = await DisposeSubscriptionAsync(_outputSubscription);
    }

    private async Task OnSourceCursorAsync(int position)
    {
        if (_outputEditorId is null ||
            _inputToOutput.IsDefault ||
            !_inputToOutput.TryFind(position, out _, out var outputSpan))
        {
            return;
        }

        try
        {
            await interop.SetSelectionAsync(_outputEditorId, outputSpan.Start, outputSpan.End);
        }
        catch (JSException)
        {
        }
    }

    private async Task OnOutputCursorAsync(int position)
    {
        if (_sourceEditorId is null ||
            _outputToInput.IsDefault ||
            !_outputToInput.TryFind(position, out _, out var inputSpan))
        {
            return;
        }

        try
        {
            await interop.SetSelectionAsync(_sourceEditorId, inputSpan.Start, inputSpan.End);
        }
        catch (JSException)
        {
        }
    }

    private async Task<IAsyncDisposable?> ReplaceSubscriptionAsync(
        IAsyncDisposable? previous,
        string editorId,
        Func<int, Task> handler)
    {
        previous = await DisposeSubscriptionAsync(previous);
        return await interop.OnDidChangeCursorPositionAsync(editorId, handler);
    }

    private static async Task<IAsyncDisposable?> DisposeSubscriptionAsync(IAsyncDisposable? subscription)
    {
        if (subscription is null)
        {
            return null;
        }

        try
        {
            await subscription.DisposeAsync();
        }
        catch (JSException)
        {
        }

        return null;
    }
}
