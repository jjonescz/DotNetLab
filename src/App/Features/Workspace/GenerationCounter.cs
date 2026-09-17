namespace DotNetLab.Features.Workspace;

internal struct GenerationCounter
{
    private int _value;

    public int Current => _value;

    public int Begin() => ++_value;

    public bool IsCurrent(int generation) => generation == _value;
}
