using System.Collections.Concurrent;
using System.Reflection;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Editor.Cutscenes;

/// <summary>
/// Finds and reads the float members that a property track can animate. The sequencer uses it to list the members of a
/// component, and to read the current value of a member when an author adds a key.
/// </summary>
public static class CutsceneFloatMemberAccess
{
    private static readonly ConcurrentDictionary<Type, string[]> s_membersByType = new();

    /// <summary>
    /// Returns the names of the public instance float properties of a component type that have a public getter and setter.
    /// The result is cached for each type, so a call in each panel frame does not allocate after the first.
    /// </summary>
    public static IReadOnlyList<string> GetFloatMembers(Type componentType)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        return s_membersByType.GetOrAdd(componentType, static type =>
        {
            List<string> names = [];
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.PropertyType == typeof(float) &&
                    property.GetGetMethod() is not null &&
                    property.GetSetMethod() is not null &&
                    property.GetIndexParameters().Length == 0)
                {
                    names.Add(property.Name);
                }
            }

            names.Sort(StringComparer.Ordinal);
            return [.. names];
        });
    }

    /// <summary>
    /// Reads a float member of the first component on a node whose type name is <paramref name="componentType"/>. The
    /// member path can contain dots for nested members.
    /// </summary>
    /// <returns>True when the component and a float member at the path exist.</returns>
    public static bool TryReadFloat(SceneNode node, string componentType, string memberPath, out float value)
    {
        value = 0.0f;
        ArgumentNullException.ThrowIfNull(node);
        XRComponent? component = FindComponent(node, componentType);
        if (component is null)
            return false;

        object? current = component;
        foreach (string part in memberPath.Split('.'))
        {
            if (current is null)
                return false;

            PropertyInfo? property = current.GetType().GetProperty(part, BindingFlags.Instance | BindingFlags.Public);
            if (property?.GetGetMethod() is null)
                return false;

            current = property.GetValue(current);
        }

        if (current is not float result)
            return false;

        value = result;
        return true;
    }

    /// <summary>Reads the vertical field of view of the perspective camera on a node, in degrees.</summary>
    /// <returns>True when the node has a camera with perspective parameters.</returns>
    public static bool TryReadFieldOfView(SceneNode node, out float degrees)
    {
        degrees = 0.0f;
        if (node.GetComponent<CameraComponent>()?.CameraParameters is not XRPerspectiveCameraParameters perspective)
            return false;

        degrees = perspective.VerticalFieldOfView;
        return true;
    }

    private static XRComponent? FindComponent(SceneNode node, string componentType)
    {
        foreach (XRComponent component in node.Components)
        {
            Type type = component.GetType();
            if (string.Equals(type.Name, componentType, StringComparison.Ordinal) ||
                string.Equals(type.FullName, componentType, StringComparison.Ordinal))
            {
                return component;
            }
        }

        return null;
    }
}
