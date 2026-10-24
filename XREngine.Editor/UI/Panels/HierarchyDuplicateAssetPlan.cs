using System.Reflection;
using System.Runtime.CompilerServices;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Scene;
using YamlDotNet.Serialization;

namespace XREngine.Editor;

/// <summary>
/// Shares direct serialized asset fields and proven auto-properties without editing borrowed containers.
/// Other asset-bearing profiles require an explicit accessor before ordinary duplication is allowed.
/// </summary>
internal sealed class HierarchyDuplicateAssetPlan
{
    private readonly HierarchyDuplicateSource _source;
    private readonly HierarchyDuplicateAssetSlot[] _slots;

    internal HierarchyDuplicateAssetPlan(HierarchyDuplicateSource source)
    {
        _source = source;
        List<HierarchyDuplicateAssetSlot> slots = [];
        for (int index = 0; index < source.Targets.Length; index++)
        {
            XRWorldObjectBase target = source.Targets[index];
            Type type = target.GetType();
            foreach (FieldInfo field in type.GetRuntimeFields())
            {
                if (!field.IsPublic || field.IsStatic || field.IsDefined(typeof(YamlIgnoreAttribute), true) ||
                    typeof(Delegate).IsAssignableFrom(field.FieldType) || !MayContainAssets(field.FieldType))
                    continue;
                if (field.IsInitOnly) throw Unsupported(type, field.Name);
                Add(index, field, null);
            }
            foreach (PropertyInfo property in type.GetRuntimeProperties())
            {
                if (property.GetMethod is null || property.SetMethod is null || property.GetMethod.IsStatic ||
                    property.GetIndexParameters().Length != 0 || property.IsDefined(typeof(YamlIgnoreAttribute), true) ||
                    typeof(Delegate).IsAssignableFrom(property.PropertyType) || IsStructuralBridge(property) ||
                    !MayContainAssets(property.PropertyType))
                    continue;
                FieldInfo? field = AutoPropertyField(property) ?? KnownNullBridge(property);
                if (field is null) throw Unsupported(type, property.Name);
                Add(index, field, property);
            }
        }
        _slots = [.. slots];
        RequireCurrent();

        void Add(int target, FieldInfo field, PropertyInfo? property)
        {
            object? value = field.GetValue(source.Targets[target]);
            if (value is not null && value is not XRAsset)
                throw Unsupported(source.Targets[target].GetType(), property?.Name ?? field.Name);
            slots.Add(new(target, field, property, value,
                value is XRAsset asset ? ObjectConstructionIdentity.Capture(asset) : null));
        }
    }

    internal void RequireCurrent()
    {
        _source.RequireCurrent();
        foreach (HierarchyDuplicateAssetSlot slot in _slots)
            slot.RequireSource(_source.Targets[slot.Target]);
    }

    internal Action Apply(XRWorldObjectBase[] cloneTargets, Action requireBoundary)
    {
        object?[] previous = new object?[_slots.Length];
        for (int index = 0; index < _slots.Length; index++)
            previous[index] = _slots[index].Field.GetValue(cloneTargets[_slots[index].Target]);
        int completed = 0;
        void RequireSlots()
        {
            RequireCurrent();
            for (int index = 0; index < _slots.Length; index++)
            {
                HierarchyDuplicateAssetSlot slot = _slots[index];
                object? expected = index < completed ? slot.Value : previous[index];
                if (!ReferenceEquals(slot.Field.GetValue(cloneTargets[slot.Target]), expected))
                    throw new InvalidOperationException("A clone asset slot changed during duplicate configuration.");
            }
        }
        for (int index = 0; index < _slots.Length; index++)
        {
            requireBoundary();
            RequireSlots();
            HierarchyDuplicateAssetSlot slot = _slots[index];
            slot.Assign(cloneTargets[slot.Target]);
            completed++;
            requireBoundary();
            RequireSlots();
        }
        return RequireSlots;
    }

    private static FieldInfo? AutoPropertyField(PropertyInfo property)
    {
        if (property.GetMethod!.IsVirtual || property.SetMethod!.IsVirtual ||
            !property.GetMethod.IsDefined(typeof(CompilerGeneratedAttribute), false) ||
            !property.SetMethod.IsDefined(typeof(CompilerGeneratedAttribute), false))
            return null;
        FieldInfo? field = property.DeclaringType!.GetField($"<{property.Name}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        return field is { IsInitOnly: false } && field.FieldType == property.PropertyType &&
            field.IsDefined(typeof(CompilerGeneratedAttribute), false) ? field : null;
    }

    private static FieldInfo? KnownNullBridge(PropertyInfo property)
    {
        // These canonical nonvirtual bridges have raw storage but can run change observers.
        string? field = property.DeclaringType == typeof(SceneNode) && property.Name == nameof(SceneNode.Prefab)
            ? "_prefab"
            : property.DeclaringType == typeof(XRWorldObjectBase) && property.Name == nameof(XRWorldObjectBase.UserData)
                ? "_userData" : null;
        return field is null ? null : property.DeclaringType!.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
    }

    private static bool IsStructuralBridge(PropertyInfo property)
        => property.DeclaringType == typeof(SceneNode) && property.Name is
            nameof(SceneNode.Transform) or nameof(SceneNode.ComponentsSerialized) or nameof(SceneNode.ChildNodesSerialized);

    private static bool MayContainAssets(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
            type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) ||
            typeof(Delegate).IsAssignableFrom(type))
            return false;
        if (type.IsValueType)
            return type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(field => MayContainAssets(field.FieldType));
        return true;
    }

    private static InvalidOperationException Unsupported(Type type, string member)
        => new($"Ordinary Duplicate cannot prove shared asset access for '{type.Name}.{member}'. Use Deep Duplicate for this hierarchy.");
}
