using System.Reflection;
using System.Runtime.CompilerServices;

namespace XREngine.Editor.UI;

/// <summary>Reads a compiler-owned list field without running a property getter.</summary>
internal sealed class EditorListSourceAccessor
{
    private readonly FieldInfo _field;

    private EditorListSourceAccessor(FieldInfo field) => _field = field;

    public static EditorListSourceAccessor? For(PropertyInfo property)
    {
        MethodInfo? getter = property.GetMethod;
        if (getter is null || getter.IsStatic || getter.IsVirtual && !getter.IsFinal ||
            property.DeclaringType is null)
            return null;
        FieldInfo? field = property.DeclaringType.GetField($"<{property.Name}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null || field.FieldType != property.PropertyType ||
            !field.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) ||
            !getter.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            return null;
        return new(field);
    }

    public bool IsCurrent(object owner, object expected)
        => ReferenceEquals(_field.GetValue(owner), expected);
}
