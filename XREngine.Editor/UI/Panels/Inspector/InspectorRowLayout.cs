using System.Reflection;

namespace XREngine.Editor;

/// <summary>
/// Rows of the stacked inspector view of one scene node: the property order, the object that each row edits,
/// the display names that replace property names, and the rows that start a section.
/// </summary>
internal sealed class InspectorRowLayout
{
    public List<PropertyInfo> Properties { get; } = [];
    public Dictionary<PropertyInfo, object?[]> Targets { get; } = [];
    public Dictionary<PropertyInfo, string> Names { get; } = [];
    public HashSet<PropertyInfo> Headers { get; } = [];

    /// <summary>Adds a row that edits <paramref name="target"/>. Returns false when the property is already listed.</summary>
    public bool Add(PropertyInfo property, object target)
    {
        if (Targets.ContainsKey(property))
            return false;
        Properties.Add(property);
        Targets.Add(property, [target]);
        return true;
    }
}
