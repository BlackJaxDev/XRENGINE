using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using XREngine.Components;

namespace XREngine;

/// <summary>
/// Finds the component that owns a tick callback: the callback's target, or the
/// component captured by a compiler-generated closure, such as the one an
/// animation tick registration wraps around the caller's delegate.
/// </summary>
internal static class RuntimeTickCallbackOwner
{
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> s_closureOwnerFields = new();

    /// <summary>
    /// Returns the owning component, or null for a callback no component owns.
    /// A closure's candidate fields are found once per closure type and cached.
    /// </summary>
    public static XRComponent? Resolve(WorldTick callback)
    {
        object? target = callback.Target;
        if (target is XRComponent component)
            return component;
        if (target is null)
            return null;

        FieldInfo[] fields = s_closureOwnerFields.GetOrAdd(target.GetType(), static type => FindClosureOwnerFields(type));
        for (int index = 0; index < fields.Length; ++index)
        {
            if (fields[index].GetValue(target) is XRComponent captured)
                return captured;
        }

        return null;
    }

    /// <summary>
    /// The fields of a compiler-generated closure type that can hold a component;
    /// none for any other type, whose fields are not a statement of ownership.
    /// </summary>
    private static FieldInfo[] FindClosureOwnerFields(Type type)
    {
        if (!type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            return [];

        List<FieldInfo> fields = [];
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.FieldType.IsAssignableFrom(typeof(XRComponent)) || typeof(XRComponent).IsAssignableFrom(field.FieldType))
                fields.Add(field);
        }

        return [.. fields];
    }
}
