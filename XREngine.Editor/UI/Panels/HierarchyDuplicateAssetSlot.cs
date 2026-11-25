using System.Reflection;
using XREngine.Data.Core;

namespace XREngine.Editor;

/// <summary>Freezes one callback-free source field and its exact clone destination.</summary>
internal sealed class HierarchyDuplicateAssetSlot(int target, FieldInfo field, PropertyInfo? property,
    object? value, ObjectConstructionIdentity? asset)
{
    internal int Target { get; } = target;
    internal FieldInfo Field { get; } = field;
    internal PropertyInfo? Property { get; } = property;
    internal object? Value { get; } = value;

    internal void RequireSource(XRWorldObjectBase owner)
    {
        if (!ReferenceEquals(Field.GetValue(owner), Value) ||
            asset is { } identity && (!identity.IsCurrent || identity.Target.IsDestroyed || identity.Target.IsDestroyQueued))
            throw new InvalidOperationException($"The duplicate source changed at '{Field.DeclaringType?.Name}.{Property?.Name ?? Field.Name}'.");
    }

    internal void Assign(XRWorldObjectBase owner)
    {
        if (Property is null)
            Field.SetValue(owner, Value);
        else
            Property.SetMethod!.Invoke(owner, BindingFlags.DoNotWrapExceptions, null, [Value], null);
    }
}
