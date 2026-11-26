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
        => BuildStackedNodeLayout(objects, null);

    /// <summary>Section key of the transform section. Component sections use the component type name.</summary>
    internal const string TransformSectionKey = "Transform";

    /// <param name="objects">The inspected objects.</param>
    /// <param name="collapsed">Keys of the sections that list only their header row.</param>
    private static InspectorRowLayout? BuildStackedNodeLayout(object?[]? objects, IReadOnlySet<string>? collapsed)
    {
        if (objects is not [Scene.SceneNode node] || node.IsDestroyed)
            return null;
        InspectorRowLayout layout = new();
        foreach (PropertyInfo property in GetMatchingProperties(objects))
        {
            layout.Add(property, node);
            if (property.Name == nameof(Scene.SceneNode.Transform))
            {
                layout.Headers.Add(property);
                layout.SectionKeys[property] = TransformSectionKey;
                // ImGui shows the transform type, so the header names it.
                layout.Names[property] = SectionTitle(collapsed, TransformSectionKey,
                    node.Transform is { } shown ? $"Transform ({shown.GetType().Name.SplitCamelCase()})" : "Transform");
            }
        }

        if (node.Transform is TransformBase transform && collapsed?.Contains(TransformSectionKey) != true)
            AddOwnMembers(layout, transform, typeof(TransformBase));

        Dictionary<Type, int> typeCounts = [];
        foreach (XRComponent component in node.Components)
        {
            if (component is null || component.IsDestroyed)
                continue;
            Type type = component.GetType();
            // The first component of a type uses the shared property objects. Later ones use scoped copies,
            // so each component owns its rows.
            int ordinal = typeCounts.TryGetValue(type, out int count) ? count : 0;
            typeCounts[type] = ordinal + 1;
            object? scope = ordinal == 0 ? null : component;
            if (type.GetProperty(nameof(XRComponent.IsActive), BindingFlags.Public | BindingFlags.Instance) is not { } shared)
                continue;
            PropertyInfo header = scope is null ? shared : InspectorScopedPropertyInfo.For(scope, shared);
            if (!layout.Add(header, component))
                continue;
            layout.Headers.Add(header);
            string key = ordinal == 0 ? type.FullName ?? type.Name : $"{type.FullName ?? type.Name}#{ordinal}";
            layout.SectionKeys[header] = key;
            layout.Names[header] = SectionTitle(collapsed, key,
                string.IsNullOrWhiteSpace(component.Name) || component.Name == type.Name
                    ? type.Name.SplitCamelCase()
                    : $"{component.Name} ({type.Name.SplitCamelCase()})");
            if (collapsed?.Contains(key) != true)
                AddOwnMembers(layout, component, typeof(XRComponent), scope);
        }
        return layout;
    }

    /// <summary>
    /// Returns the title of a section header row. The row draws its own disclosure chevron, so the title has
    /// no mark. <paramref name="collapsed"/> and <paramref name="key"/> are kept for callers that name sections.
    /// </summary>
    private static string SectionTitle(IReadOnlySet<string>? collapsed, string key, string title)
        => title;

    /// <summary>Returns true when the section that <paramref name="key"/> names lists only its header row.</summary>
    private bool IsStackedSectionCollapsed(string key) => _collapsedStackSections.Contains(key);

    /// <summary>
    /// Opens or collapses the section that <paramref name="property"/> starts, when it is a section header row.
    /// Clicking a header row in the stacked view calls this.
    /// </summary>
    private void ToggleStackedSection(ulong itemId)
    {
        if (_virtualPropertyProvider is not { } provider || !provider.TryGetIndexOfItemId(itemId, out int index) ||
            provider.SectionKeyFor(provider.GetItem(index)) is not { } key)
            return;
        if (!_collapsedStackSections.Remove(key))
            _collapsedStackSections.Add(key);
        RefreshVirtualProperties();
    }

    private readonly HashSet<string> _collapsedStackSections = [];

    /// <summary>
    /// Adds the inspectable members that <paramref name="target"/>'s type declares below <paramref name="baseType"/>.
    /// The base members are engine runtime state, such as the owner node, world, and lifetime flags.
    /// </summary>
    /// <param name="scope">When set, rows use scoped property copies owned by this object.</param>
    private static void AddOwnMembers(InspectorRowLayout layout, object target, Type baseType, object? scope = null)
    {
        foreach (PropertyInfo property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.DeclaringType is not { } declaring || declaring == baseType ||
                declaring.IsAssignableFrom(baseType) || !IsInspectableProperty(target, property))
                continue;
            layout.Add(scope is null ? property : InspectorScopedPropertyInfo.For(scope, property), target);
        }
    }
}
