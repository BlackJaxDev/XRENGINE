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

    private static InspectorRowLayout? BuildStackedNodeLayout(object?[]? objects, IReadOnlySet<string>? collapsed)
        => BuildStackedNodeLayout(objects, collapsed, null);

    /// <param name="objects">The inspected objects.</param>
    /// <param name="collapsed">Keys of the sections that list only their header row.</param>
    /// <param name="search">
    /// Member search text. When it is set, the layout lists only the members whose name, display name, or
    /// category contains it, and only the sections that have such members or whose title contains it. A
    /// section whose title matches lists all its members. Searching opens every section, like the ImGui
    /// inspector.
    /// </param>
    private static InspectorRowLayout? BuildStackedNodeLayout(object?[]? objects, IReadOnlySet<string>? collapsed,
        string? search)
    {
        if (objects is not [Scene.SceneNode node] || node.IsDestroyed)
            return null;
        if (string.IsNullOrWhiteSpace(search))
            search = null;
        else
            collapsed = null;
        InspectorRowLayout layout = new();
        PropertyInfo? transformHeader = null;
        foreach (PropertyInfo property in GetMatchingProperties(objects))
        {
            if (property.Name == nameof(Scene.SceneNode.Transform))
            {
                transformHeader = property;
                continue;
            }
            if (search is null || MatchesMemberSearch(property, search))
                layout.Add(property, node);
        }

        if (transformHeader is not null)
        {
            // ImGui shows the transform type, so the header names it.
            string title = node.Transform is { } shown ? $"Transform ({shown.GetType().Name.SplitCamelCase()})" : "Transform";
            List<PropertyInfo> members = node.Transform is TransformBase transform
                ? CollectOwnMembers(transform, typeof(TransformBase), null)
                : [];
            AddSection(layout, transformHeader, node, node.Transform, TransformSectionKey, title, members, collapsed, search);
        }

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
            string key = ordinal == 0 ? type.FullName ?? type.Name : $"{type.FullName ?? type.Name}#{ordinal}";
            string title = string.IsNullOrWhiteSpace(component.Name) || component.Name == type.Name
                ? type.Name.SplitCamelCase()
                : $"{component.Name} ({type.Name.SplitCamelCase()})";
            AddSection(layout, header, component, component, key, title,
                CollectOwnMembers(component, typeof(XRComponent), scope), collapsed, search);
        }
        return layout;
    }

    /// <summary>
    /// Adds one section: its header row, which edits <paramref name="headerTarget"/>, then the member rows,
    /// which edit <paramref name="memberTarget"/>. With a search, the section is left out when neither its
    /// title nor a member matches, and a title match keeps all members.
    /// </summary>
    private static void AddSection(InspectorRowLayout layout, PropertyInfo header, object headerTarget,
        object? memberTarget, string key, string title, List<PropertyInfo> members,
        IReadOnlySet<string>? collapsed, string? search)
    {
        bool titleMatches = search is not null && title.Contains(search, StringComparison.OrdinalIgnoreCase);
        if (search is not null && !titleMatches)
            members.RemoveAll(member => !MatchesMemberSearch(member, search));
        if (search is not null && !titleMatches && members.Count == 0)
            return;
        if (!layout.Add(header, headerTarget))
            return;
        layout.Headers.Add(header);
        layout.SectionKeys[header] = key;
        layout.Names[header] = SectionTitle(collapsed, key, title);
        if (memberTarget is null || collapsed?.Contains(key) == true)
            return;
        foreach (PropertyInfo member in members)
            layout.Add(member, memberTarget);
    }

    /// <summary>
    /// Returns true when the member search text is in the property name, its display name, or its category.
    /// The ImGui inspector matches the same three texts.
    /// </summary>
    internal static bool MatchesMemberSearch(PropertyInfo property, string search)
    {
        PropertyInfo source = property is InspectorScopedPropertyInfo scoped ? scoped.Inner : property;
        return source.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            (ResolveName(source)?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (source.GetCustomAttribute<System.ComponentModel.CategoryAttribute>()?.Category
                .Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
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
    /// Returns the inspectable members that <paramref name="target"/>'s type declares below <paramref name="baseType"/>.
    /// The base members are engine runtime state, such as the owner node, world, and lifetime flags.
    /// </summary>
    /// <param name="scope">When set, rows use scoped property copies owned by this object.</param>
    private static List<PropertyInfo> CollectOwnMembers(object target, Type baseType, object? scope)
    {
        List<PropertyInfo> members = [];
        foreach (PropertyInfo property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.DeclaringType is not { } declaring || declaring == baseType ||
                declaring.IsAssignableFrom(baseType) || !IsInspectableProperty(target, property))
                continue;
            members.Add(scope is null ? property : InspectorScopedPropertyInfo.For(scope, property));
        }
        return members;
    }
}
