namespace DotNetLab;

[AttributeUsage(AttributeTargets.Struct | AttributeTargets.GenericParameter)]
public sealed class NonCopyableAttribute : Attribute;
