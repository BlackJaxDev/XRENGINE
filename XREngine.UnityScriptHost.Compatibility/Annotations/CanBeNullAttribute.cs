namespace JetBrains.Annotations;

/// <summary>Marks a source value that can be null. The attribute does not change execution.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter | AttributeTargets.Property |
    AttributeTargets.Field | AttributeTargets.Event | AttributeTargets.Delegate | AttributeTargets.ReturnValue |
    AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.GenericParameter)]
public sealed class CanBeNullAttribute : Attribute;
