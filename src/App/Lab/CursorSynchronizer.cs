namespace DotNetLab.Lab;

internal sealed class CursorSynchronizer(
    CursorSynchronizer.Services services,
    BlazorMonaco.Editor.Editor inputEditor,
    BlazorMonaco.Editor.Editor outputEditor)
    : IAsyncDisposable
{
    public sealed record Services(BlazorMonacoInterop Interop);

    private DocumentMapping inputToOutputMapping, outputToInputMapping;
    private IAsyncDisposable? inputCursorSubscription, outputCursorSubscription;

    public async Task InitAsync()
    {
        inputCursorSubscription = await services.Interop.OnDidChangeCursorPositionAsync(inputEditor.Id, OnInputCursorPositionChangedAsync);
        outputCursorSubscription = await services.Interop.OnDidChangeCursorPositionAsync(outputEditor.Id, OnOutputCursorPositionChangedAsync);
    }

    public async ValueTask DisposeAsync()
    {
        await inputCursorSubscription?.DisposeAsync();
        await outputCursorSubscription?.DisposeAsync();
    }

    public void Enable(CompiledFileOutputMetadata? metadata)
    {
        if (metadata is { InputToOutput: { } inputToOutput, OutputToInput: { } outputToInput })
        {
            inputToOutputMapping = DocumentMapping.Deserialize(inputToOutput);
            outputToInputMapping = DocumentMapping.Deserialize(outputToInput);
        }
        else
        {
            inputToOutputMapping = default;
            outputToInputMapping = default;
        }
    }

    private async Task OnInputCursorPositionChangedAsync(int position)
    {
        if (inputToOutputMapping.IsDefault ||
            !inputToOutputMapping.TryFind(position, out _, out var outputSpan))
        {
            return;
        }

        await services.Interop.SetSelectionAsync(outputEditor.Id, outputSpan.Start, outputSpan.End);
    }

    private async Task OnOutputCursorPositionChangedAsync(int position)
    {
        if (outputToInputMapping.IsDefault ||
            !outputToInputMapping.TryFind(position, out _, out var inputSpan))
        {
            return;
        }

        await services.Interop.SetSelectionAsync(inputEditor.Id, inputSpan.Start, inputSpan.End);
    }
}
