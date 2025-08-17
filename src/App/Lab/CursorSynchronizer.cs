using System.Runtime.Versioning;

namespace DotNetLab.Lab;

[SupportedOSPlatform("browser")]
internal sealed class CursorSynchronizer(
    CursorSynchronizer.Services services,
    BlazorMonaco.Editor.Editor inputEditor,
    BlazorMonaco.Editor.Editor outputEditor)
{
    public sealed record Services(BlazorMonacoInterop Interop);

    private DocumentMapping inputToOutputMapping, outputToInputMapping;

    public async Task InitAsync()
    {
        await services.Interop.OnDidChangeCursorPosition(inputEditor.Id, OnInputCursorPositionChanged);
        await services.Interop.OnDidChangeCursorPosition(outputEditor.Id, OnOutputCursorPositionChanged);
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

    private void OnInputCursorPositionChanged(int position)
    {
        if (inputToOutputMapping.IsDefault ||
            !inputToOutputMapping.TryFind(position, out _, out var outputSpan))
        {
            return;
        }

        BlazorMonacoInterop.SetSelectionUnsafe(outputEditor.Id, outputSpan.Start, outputSpan.End);
    }

    private void OnOutputCursorPositionChanged(int position)
    {
        if (outputToInputMapping.IsDefault ||
            !outputToInputMapping.TryFind(position, out _, out var inputSpan))
        {
            return;
        }

        BlazorMonacoInterop.SetSelectionUnsafe(inputEditor.Id, inputSpan.Start, inputSpan.End);
    }
}
