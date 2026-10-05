namespace XREngine;

/// <summary>Requests a generated typed setter for one runtime animation target member.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RuntimeAnimationBindingAttribute(Type targetType, string memberName) : Attribute
{
    public Type TargetType { get; } = targetType;
    public string MemberName { get; } = memberName;
}
