using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XREngine.Editor;

/// <summary>
/// A row key for one persistent callback of an event member. The inspector keys rows by property object, so
/// each callback gets its own property object that forwards to the event property and carries the callback
/// index. The same owner, event property, and index always return the same row, so rows keep their identity
/// when the inspector rebuilds.
/// </summary>
internal sealed class InspectorEventCallbackRow : PropertyInfo
{
    private static readonly ConditionalWeakTable<object, Dictionary<(PropertyInfo, int), InspectorEventCallbackRow>> Rows = new();

    private InspectorEventCallbackRow(PropertyInfo eventProperty, int index)
    {
        EventProperty = eventProperty;
        Index = index;
    }

    /// <summary>The event member that holds the callback.</summary>
    public PropertyInfo EventProperty { get; }

    /// <summary>The position of the callback in the event's persistent call list.</summary>
    public int Index { get; }

    /// <summary>Returns the row of callback <paramref name="index"/> of <paramref name="eventProperty"/> on <paramref name="owner"/>.</summary>
    public static InspectorEventCallbackRow For(object owner, PropertyInfo eventProperty, int index)
    {
        Dictionary<(PropertyInfo, int), InspectorEventCallbackRow> rows = Rows.GetOrCreateValue(owner);
        if (!rows.TryGetValue((eventProperty, index), out InspectorEventCallbackRow? row))
            rows.Add((eventProperty, index), row = new InspectorEventCallbackRow(eventProperty, index));
        return row;
    }

    public override string Name => $"{EventProperty.Name}[{Index}]";
    public override Type PropertyType => EventProperty.PropertyType;
    public override PropertyAttributes Attributes => EventProperty.Attributes;
    public override bool CanRead => EventProperty.CanRead;
    public override bool CanWrite => false;
    public override Type? DeclaringType => EventProperty.DeclaringType;
    public override Type? ReflectedType => EventProperty.ReflectedType;
    public override Module Module => EventProperty.Module;
    public override int MetadataToken => EventProperty.MetadataToken;
    public override MethodInfo[] GetAccessors(bool nonPublic) => EventProperty.GetAccessors(nonPublic);
    public override MethodInfo? GetGetMethod(bool nonPublic) => EventProperty.GetGetMethod(nonPublic);
    public override MethodInfo? GetSetMethod(bool nonPublic) => null;
    public override ParameterInfo[] GetIndexParameters() => [];
    public override object? GetValue(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? index, CultureInfo? culture)
        => EventProperty.GetValue(obj, invokeAttr, binder, index, culture);
    public override void SetValue(object? obj, object? value, BindingFlags invokeAttr, Binder? binder, object?[]? index, CultureInfo? culture)
        => throw new NotSupportedException("A callback row does not set its event.");
    public override object[] GetCustomAttributes(bool inherit) => EventProperty.GetCustomAttributes(inherit);
    public override object[] GetCustomAttributes(Type attributeType, bool inherit) => EventProperty.GetCustomAttributes(attributeType, inherit);
    public override bool IsDefined(Type attributeType, bool inherit) => EventProperty.IsDefined(attributeType, inherit);
    public override IList<CustomAttributeData> GetCustomAttributesData() => EventProperty.GetCustomAttributesData();
}
