using Microsoft.CodeAnalysis.Classification;

namespace DotNetLab;

internal sealed class CompiledFileOutputMetadata
{
    public ImmutableArray<ClassifiedSpan> ClassifiedSpans { get; init; }
}
