using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

namespace XREngine.Editor;

/// <summary>
/// A property object that reads and writes a public field. The inspector keys and binds rows by property
/// object, so a field member, such as an event field, is wrapped to get a row. The same field always returns
/// the same wrapper.
/// </summary>
internal sealed class InspectorFieldPropertyInfo : PropertyInfo
{
    private static readonly ConcurrentDictionary<FieldInfo, InspectorFieldPropertyInfo> Wrappers = new();

    private InspectorFieldPropertyInfo(FieldInfo field) => Field = field;

    /// <summary>The wrapped field.</summary>
    public FieldInfo Field { get; }

    /// <summary>Returns the wrapper of <paramref name="field"/>.</summary>
    public static InspectorFieldPropertyInfo For(FieldInfo field)
        => Wrappers.GetOrAdd(field, static item => new InspectorFieldPropertyInfo(item));

    public override string Name => Field.Name;
    public override Type PropertyType => Field.FieldType;
    public override PropertyAttributes Attributes => PropertyAttributes.None;
    public override bool CanRead => true;
    public override bool CanWrite => !Field.IsInitOnly && !Field.IsLiteral;
    public override Type? DeclaringType => Field.DeclaringType;
    public override Type? ReflectedType => Field.ReflectedType;
    public override Module Module => Field.Module;
    public override int MetadataToken => Field.MetadataToken;
    public override MethodInfo[] GetAccessors(bool nonPublic) => [];
    public override MethodInfo? GetGetMethod(bool nonPublic) => null;
    public override MethodInfo? GetSetMethod(bool nonPublic) => null;
    public override ParameterInfo[] GetIndexParameters() => [];
    public override object? GetValue(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? index, CultureInfo? culture)
        => Field.GetValue(obj);
    public override void SetValue(object? obj, object? value, BindingFlags invokeAttr, Binder? binder, object?[]? index, CultureInfo? culture)
        => Field.SetValue(obj, value);
    public override object[] GetCustomAttributes(bool inherit) => Field.GetCustomAttributes(inherit);
    public override object[] GetCustomAttributes(Type attributeType, bool inherit) => Field.GetCustomAttributes(attributeType, inherit);
    public override bool IsDefined(Type attributeType, bool inherit) => Field.IsDefined(attributeType, inherit);
    public override IList<CustomAttributeData> GetCustomAttributesData() => Field.GetCustomAttributesData();
}
