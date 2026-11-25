using System.Reflection;
using XREngine.Components;
using XREngine.Extensions;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public partial class InspectorPanel
{
    /// <summary>
    /// Builds the stacked view of one selected scene node, as the ImGui inspector draws it: the node summary
    /// (name, active state, layer), a Transform section with the transform's own members, and one section per
    /// component. Each component section starts with its active toggle, named after the component. Returns
    /// null when the inspector does not show exactly one scene node, so the plain property list applies.
    /// </summary>
    /// <remarks>
    /// Rows are keyed by <see cref="PropertyInfo"/>. Two components of the same type share their property
    /// objects, so only the first component of each type gets a section.
    /// </remarks>
    private static InspectorRowLayout? BuildStackedNodeLayout(object?[]? objects)
    {
        if (objects is not [Scene.SceneNode node] || node.IsDestroyed)
            return null;
        InspectorRowLayout layout = new();
        foreach (PropertyInfo property in GetMatchingProperties(objects))
        {
            layout.Add(property, node);
            if (property.Name == nameof(Scene.SceneNode.Transform))
                layout.Headers.Add(property);
        }

        if (node.Transform is TransformBase transform)
            AddOwnMembers(layout, transform, typeof(TransformBase));

        HashSet<Type> listedTypes = [];
        foreach (XRComponent component in node.Components)
        {
            if (component is null || component.IsDestroyed || !listedTypes.Add(component.GetType()))
                continue;
            Type type = component.GetType();
            if (type.GetProperty(nameof(XRComponent.IsActive), BindingFlags.Public | BindingFlags.Instance) is not { } header ||
                !layout.Add(header, component))
                continue;
            layout.Headers.Add(header);
            layout.Names[header] = string.IsNullOrWhiteSpace(component.Name) || component.Name == type.Name
                ? type.Name.SplitCamelCase()
                : $"{component.Name} ({type.Name.SplitCamelCase()})";
            AddOwnMembers(layout, component, typeof(XRComponent));
        }
        return layout;
    }

    /// <summary>
    /// Adds the inspectable members that <paramref name="target"/>'s type declares below <paramref name="baseType"/>.
    /// The base members are engine runtime state, such as the owner node, world, and lifetime flags.
    /// </summary>
    private static void AddOwnMembers(InspectorRowLayout layout, object target, Type baseType)
    {
        foreach (PropertyInfo property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.DeclaringType is not { } declaring || declaring == baseType ||
                declaring.IsAssignableFrom(baseType) || !IsInspectableProperty(target, property))
                continue;
            layout.Add(property, target);
        }
    }
}
