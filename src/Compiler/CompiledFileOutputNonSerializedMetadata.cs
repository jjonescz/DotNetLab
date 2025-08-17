using Microsoft.CodeAnalysis.Classification;

namespace DotNetLab;

internal sealed class CompiledFileOutputNonSerializedMetadata
{
    public ImmutableArray<ClassifiedSpan> ClassifiedSpans { get; init; }
}
