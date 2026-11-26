using System.Reflection;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    /// <summary>
    /// One method that a persistent event callback can call: the target object (the selected node or one of its
    /// components), the group label for the picker, the method label, and the values that
    /// <see cref="XRPersistentCall"/> stores.
    /// </summary>
    internal readonly record struct EventCallbackMethodChoice(XRObjectBase Target, string Group, string Label,
        string MethodName, string[] ParameterTypeNames, bool TupleExpanded);

    /// <summary>Returns true when <paramref name="type"/> is <see cref="XREvent"/> or <see cref="XREvent{T}"/>.</summary>
    internal static bool IsEventCallbackType(Type type) => IsXREventType(type);

    /// <summary>Returns the persistent calls of an event, or null when it has none.</summary>
    internal static List<XRPersistentCall>? GetEventCallbacks(object? eventInstance)
        => eventInstance is null ? null : GetPersistentCallsOrNull(eventInstance);

    /// <summary>Gets the scene node that a persistent call names, or null when it is missing.</summary>
    internal static SceneNode? ResolveEventCallbackNode(XRPersistentCall call) => ResolveSceneNode(call.NodeId);

    /// <summary>Writes the picker text of a persistent call: "TypeName.Method", or a "not set" marker.</summary>
    internal static string DescribeEventCallback(XRPersistentCall call) => GetPersistentCallPreview(call);

    /// <summary>
    /// Lists the methods of <paramref name="node"/> and its components that an event of
    /// <paramref name="declaredEventType"/> can call. The ImGui callback picker uses the same list.
    /// </summary>
    internal static List<EventCallbackMethodChoice> GetEventCallbackMethodChoices(SceneNode node, Type declaredEventType)
    {
        List<EventCallbackMethodChoice> choices = [];
        foreach (EventMethodOption option in BuildEventMethodOptions(node, GetEventSignatureOptions(declaredEventType)))
            choices.Add(new(option.TargetObject, option.GroupLabel, option.DisplayLabel, option.Method.Name,
                option.ParameterTypeNames, option.Signature.TupleExpanded));
        return choices;
    }

    /// <summary>
    /// Adds an empty persistent call to the event in <paramref name="property"/> of <paramref name="owner"/>. It
    /// creates the event first when the property is null. The change is recorded for undo.
    /// </summary>
    internal static void AddEventCallback(object owner, PropertyInfo property)
    {
        object? instance = property.GetValue(owner);
        if (instance is null)
        {
            instance = Activator.CreateInstance(property.PropertyType);
            if (instance is null)
                return;
            using (Undo.TrackChange($"Create {property.Name}", owner))
                property.SetValue(owner, instance);
        }
        List<XRPersistentCall> calls = GetOrCreatePersistentCalls(instance, owner);
        XRPersistentCall call = new();
        calls.Add(call);
        Undo.RecordStructuralChange("Add Callback",
            undoAction: () => { calls.Remove(call); NotifyInspectorValueEdited(owner); },
            redoAction: () => { calls.Add(call); NotifyInspectorValueEdited(owner); });
        NotifyInspectorValueEdited(owner);
    }

    /// <summary>Removes the persistent call at <paramref name="index"/>. The change is recorded for undo.</summary>
    internal static void RemoveEventCallback(object owner, List<XRPersistentCall> calls, int index)
    {
        if ((uint)index >= (uint)calls.Count)
            return;
        XRPersistentCall removed = calls[index];
        calls.RemoveAt(index);
        Undo.RecordStructuralChange("Remove Callback",
            undoAction: () => { calls.Insert(index, removed); NotifyInspectorValueEdited(owner); },
            redoAction: () => { calls.Remove(removed); NotifyInspectorValueEdited(owner); });
        NotifyInspectorValueEdited(owner);
    }

    /// <summary>Sets the node of a persistent call and clears its method, as the ImGui node picker does.</summary>
    internal static void SetEventCallbackNode(object owner, XRPersistentCall call, SceneNode node)
    {
        using (Undo.TrackChange("Set Callback Node", owner))
        {
            call.NodeId = node.ID;
            call.TargetObjectId = Guid.Empty;
            call.MethodName = null;
            call.ParameterTypeNames = null;
            call.UseTupleExpansion = false;
        }
        NotifyInspectorValueEdited(owner);
    }

    /// <summary>Sets the method of a persistent call, or clears it when <paramref name="choice"/> is null.</summary>
    internal static void SetEventCallbackMethod(object owner, XRPersistentCall call, EventCallbackMethodChoice? choice)
    {
        using (Undo.TrackChange(choice is null ? "Clear Callback Method" : "Set Callback Method", owner))
        {
            call.TargetObjectId = choice?.Target.ID ?? Guid.Empty;
            call.MethodName = choice?.MethodName;
            call.ParameterTypeNames = choice?.ParameterTypeNames;
            call.UseTupleExpansion = choice?.TupleExpanded ?? false;
        }
        NotifyInspectorValueEdited(owner);
    }
}
