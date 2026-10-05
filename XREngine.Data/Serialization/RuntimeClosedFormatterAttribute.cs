namespace XREngine;

/// <summary>Requests a statically rooted formatter for one closed collection, nullable, or tuple type.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RuntimeClosedFormatterAttribute(Type closedType) : Attribute
{
    public Type ClosedType { get; } = closedType;
}
