namespace XREngine;

/// <summary>Roots a public player member as a typed generated accessor.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RuntimeMemberAccessAttribute(Type targetType, string memberName) : Attribute
{
    public Type TargetType { get; } = targetType;
    public string MemberName { get; } = memberName;
}
