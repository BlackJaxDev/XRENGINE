using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Makes deep copies of render command trees for the Workbench draft. A copy has new command and container objects,
/// so an edit to the copy never changes the source tree.
/// </summary>
/// <remarks>
/// The cloner copies each public property that has a public getter and setter. Child containers, switch cases,
/// branch conditions, lists, and arrays are copied. Delegates and asset references (materials, meshes) stay shared,
/// because the draft does not edit them. A push command owns its pop command, so a pop command in the source tree
/// maps to the pop command of the copied push command.
/// </remarks>
public static class RenderCommandGraphCloner
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> s_properties = new();

    /// <summary>Returns a deep copy of <paramref name="source"/>. The copy has no parent pipeline.</summary>
    public static ViewportRenderCommandContainer CloneContainer(ViewportRenderCommandContainer source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Dictionary<ViewportRenderCommand, ViewportRenderCommand> popMap = new(ReferenceEqualityComparer.Instance);
        return CloneContainer(source, popMap);
    }

    /// <summary>
    /// Returns deep copies of <paramref name="commands"/>, in order. A pop command in the list maps to the pop command
    /// of a copied push command in the same list.
    /// </summary>
    public static List<ViewportRenderCommand> CloneCommands(IReadOnlyList<ViewportRenderCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        Dictionary<ViewportRenderCommand, ViewportRenderCommand> popMap = new(ReferenceEqualityComparer.Instance);
        List<ViewportRenderCommand> copies = new(commands.Count);
        for (int i = 0; i < commands.Count; i++)
            copies.Add(CloneCommand(commands[i], popMap));
        return copies;
    }

    /// <summary>Returns a deep copy of one command and its child containers.</summary>
    public static ViewportRenderCommand CloneCommand(ViewportRenderCommand source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Dictionary<ViewportRenderCommand, ViewportRenderCommand> popMap = new(ReferenceEqualityComparer.Instance);
        return CloneCommand(source, popMap);
    }

    /// <summary>Creates a new command of <paramref name="type"/> with default property values.</summary>
    public static ViewportRenderCommand CreateCommand(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (ViewportRenderCommandContainer.TryCreateRegisteredCommand(type, out ViewportRenderCommand? registered) && registered is not null)
            return registered;

        return Activator.CreateInstance(type) as ViewportRenderCommand
            ?? throw new ArgumentException($"Type '{type.Name}' is not a render command with a public parameterless constructor.", nameof(type));
    }

    /// <summary>
    /// Returns the public properties of <paramref name="type"/> that the cloner copies. The Workbench uses the same
    /// list to find resource name properties.
    /// </summary>
    public static PropertyInfo[] GetCopiedProperties(Type type)
        => s_properties.GetOrAdd(type, static t => FindCopiedProperties(t));

    private static PropertyInfo[] FindCopiedProperties(Type type)
    {
        List<PropertyInfo> properties = [];
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;
            if (property.GetSetMethod() is null || property.GetGetMethod() is null)
                continue;
            if (property.Name is nameof(ViewportRenderCommand.CommandContainer) or nameof(ViewportRenderCommand.ParentPipeline))
                continue;

            properties.Add(property);
        }

        return [.. properties];
    }

    private static ViewportRenderCommandContainer CloneContainer(
        ViewportRenderCommandContainer source,
        Dictionary<ViewportRenderCommand, ViewportRenderCommand> popMap)
    {
        ViewportRenderCommandContainer copy = new()
        {
            BranchResources = source.BranchResources,
        };

        for (int i = 0; i < source.Count; i++)
            copy.Add(CloneCommand(source[i], popMap));
        return copy;
    }

    private static ViewportRenderCommand CloneCommand(
        ViewportRenderCommand source,
        Dictionary<ViewportRenderCommand, ViewportRenderCommand> popMap)
    {
        ViewportRenderCommand copy = popMap.Remove(source, out ViewportRenderCommand? mappedPop)
            ? mappedPop
            : CreateCommand(source.GetType());

        if (source is ViewportStateRenderCommandBase push && copy is ViewportStateRenderCommandBase pushCopy)
            popMap[push.PairedPopCommand] = pushCopy.PairedPopCommand;

        PropertyInfo[] properties = GetCopiedProperties(source.GetType());
        for (int i = 0; i < properties.Length; i++)
        {
            PropertyInfo property = properties[i];
            object? value = property.GetValue(source);
            property.SetValue(copy, CloneValue(value, popMap));
        }

        return copy;
    }

    private static object? CloneValue(object? value, Dictionary<ViewportRenderCommand, ViewportRenderCommand> popMap)
    {
        switch (value)
        {
            case null:
                return null;
            case string or Delegate:
                return value;
            case ViewportRenderCommandContainer container:
                return CloneContainer(container, popMap);
            case RenderBranchCondition condition:
                return condition.Clone();
            case Array array when array.GetType().GetElementType() is { IsValueType: true } || array is string[]:
                return array.Clone();
            case IDictionary<int, ViewportRenderCommandContainer> cases:
            {
                Dictionary<int, ViewportRenderCommandContainer> copy = new(cases.Count);
                foreach (KeyValuePair<int, ViewportRenderCommandContainer> pair in cases)
                    copy[pair.Key] = CloneContainer(pair.Value, popMap);
                return copy;
            }
            case IList list when value.GetType().IsGenericType && Activator.CreateInstance(value.GetType()) is IList copy:
            {
                for (int i = 0; i < list.Count; i++)
                    copy.Add(list[i] is string or ValueType ? list[i] : CloneValue(list[i], popMap));
                return copy;
            }
        }

        // Value types copy by assignment. Other reference types (materials, meshes, providers) are shared assets.
        return value;
    }
}
