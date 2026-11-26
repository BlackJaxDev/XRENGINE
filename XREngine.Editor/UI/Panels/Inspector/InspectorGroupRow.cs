using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XREngine.Editor;

/// <summary>
/// A row key for a group title inside a section, such as "Shadow Map" in a light section. The inspector keys
/// rows by property object, so each group title gets its own property object. It has no value; the row shows
/// only the title. The same owner and title always return the same row.
/// </summary>
internal sealed class InspectorGroupRow : PropertyInfo
{
    private static readonly ConditionalWeakTable<object, Dictionary<string, InspectorGroupRow>> Rows = new();

    private InspectorGroupRow(string title) => Title = title;

    /// <summary>The group title that the row shows.</summary>
    public string Title { get; }

    /// <summary>Returns the row of group <paramref name="title"/> for <paramref name="owner"/>.</summary>
    public static InspectorGroupRow For(object owner, string title)
    {
        Dictionary<string, InspectorGroupRow> rows = Rows.GetOrCreateValue(owner);
        if (!rows.TryGetValue(title, out InspectorGroupRow? row))
            rows.Add(title, row = new InspectorGroupRow(title));
        return row;
    }

    public override string Name => Title;
    public override Type PropertyType => typeof(void);
    public override PropertyAttributes Attributes => PropertyAttributes.None;
    public override bool CanRead => false;
    public override bool CanWrite => false;
    public override Type? DeclaringType => null;
    public override Type? ReflectedType => null;
    public override Module Module => typeof(InspectorGroupRow).Module;
    public override int MetadataToken => 0;
    public override MethodInfo[] GetAccessors(bool nonPublic) => [];
    public override MethodInfo? GetGetMethod(bool nonPublic) => null;
    public override MethodInfo? GetSetMethod(bool nonPublic) => null;
    public override ParameterInfo[] GetIndexParameters() => [];
    public override object? GetValue(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? index, CultureInfo? culture)
        => null;
    public override void SetValue(object? obj, object? value, BindingFlags invokeAttr, Binder? binder, object?[]? index, CultureInfo? culture)
        => throw new NotSupportedException("A group title row has no value.");
    public override object[] GetCustomAttributes(bool inherit) => [];
    public override object[] GetCustomAttributes(Type attributeType, bool inherit) => [];
    public override bool IsDefined(Type attributeType, bool inherit) => false;
    public override IList<CustomAttributeData> GetCustomAttributesData() => [];
}
