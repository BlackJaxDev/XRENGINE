using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XREngine.Editor;

/// <summary>
/// A property object that forwards every member to another <see cref="PropertyInfo"/> but has its own
/// identity. The stacked inspector keys rows by property object. Two components of the same type share
/// their property objects, so the second and later components get scoped copies to own their rows.
/// </summary>
internal sealed class InspectorScopedPropertyInfo(PropertyInfo inner) : PropertyInfo
{
    private static readonly ConditionalWeakTable<object, Dictionary<PropertyInfo, InspectorScopedPropertyInfo>> Scopes = new();

    /// <summary>The forwarded property.</summary>
    public PropertyInfo Inner { get; } = inner;

    /// <summary>
    /// Returns the scoped copy of <paramref name="property"/> for <paramref name="owner"/>. The same owner and
    /// property always return the same copy, so rows keep their identity when the inspector rebuilds.
    /// </summary>
    public static PropertyInfo For(object owner, PropertyInfo property)
    {
        Dictionary<PropertyInfo, InspectorScopedPropertyInfo> scoped = Scopes.GetOrCreateValue(owner);
        if (!scoped.TryGetValue(property, out InspectorScopedPropertyInfo? copy))
            scoped.Add(property, copy = new InspectorScopedPropertyInfo(property));
        return copy;
    }

    public override string Name => Inner.Name;
    public override Type PropertyType => Inner.PropertyType;
    public override PropertyAttributes Attributes => Inner.Attributes;
    public override bool CanRead => Inner.CanRead;
    public override bool CanWrite => Inner.CanWrite;
    public override Type? DeclaringType => Inner.DeclaringType;
    public override Type? ReflectedType => Inner.ReflectedType;
    public override Module Module => Inner.Module;
    public override int MetadataToken => Inner.MetadataToken;
    public override MethodInfo[] GetAccessors(bool nonPublic) => Inner.GetAccessors(nonPublic);
    public override MethodInfo? GetGetMethod(bool nonPublic) => Inner.GetGetMethod(nonPublic);
    public override MethodInfo? GetSetMethod(bool nonPublic) => Inner.GetSetMethod(nonPublic);
    public override ParameterInfo[] GetIndexParameters() => Inner.GetIndexParameters();
    public override object? GetValue(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? index, CultureInfo? culture)
        => Inner.GetValue(obj, invokeAttr, binder, index, culture);
    public override void SetValue(object? obj, object? value, BindingFlags invokeAttr, Binder? binder, object?[]? index, CultureInfo? culture)
        => Inner.SetValue(obj, value, invokeAttr, binder, index, culture);
    public override object[] GetCustomAttributes(bool inherit) => Inner.GetCustomAttributes(inherit);
    public override object[] GetCustomAttributes(Type attributeType, bool inherit) => Inner.GetCustomAttributes(attributeType, inherit);
    public override bool IsDefined(Type attributeType, bool inherit) => Inner.IsDefined(attributeType, inherit);
    public override IList<CustomAttributeData> GetCustomAttributesData() => Inner.GetCustomAttributesData();
}
