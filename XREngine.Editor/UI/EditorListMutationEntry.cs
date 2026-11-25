using System.Collections;
using System.Reflection;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>Stores the exact list and owner state for one edit.</summary>
internal sealed class EditorListMutationEntry
{
    public required IList List { get; init; }
    public required PropertyInfo Property { get; init; }
    public required EditorListSourceAccessor SourceAccessor { get; init; }
    public required object[] Owners { get; init; }
    public required object?[] OwnerWorlds { get; init; }
    public required object?[] OwnerDocuments { get; init; }
    public required SceneNode?[] OwnerNodes { get; init; }
    public required object?[] Before { get; init; }
    public required object?[] After { get; set; }
    public required object BeforeState { get; init; }
    public required object AfterState { get; init; }
    public required EEditorListMutation Operation { get; init; }
    public required int Index { get; init; }

    public bool IsOwnerCurrent()
    {
        if (!IsOwnerLocationCurrent())
            return false;
        for (int index = 0; index < Owners.Length; index++)
            if (!ReferenceEquals(Property.GetValue(Owners[index]), List))
                return false;
        return true;
    }

    public bool IsOwnerLocationCurrent()
    {
        for (int index = 0; index < Owners.Length; index++)
        {
            object owner = Owners[index];
            if (owner is XRObjectBase xrOwner && (xrOwner.IsDestroyed || xrOwner.IsDestroyQueued) ||
                owner is XRWorldObjectBase worldOwner &&
                !ReferenceEquals(worldOwner.World, OwnerWorlds[index]) ||
                owner is XRWorldObjectBase { World: RuntimeWorld { IsDisposing: true } } ||
                !ReferenceEquals(EditorListMutation.DocumentOf(owner), OwnerDocuments[index]) ||
                owner is XRComponent component &&
                (!ReferenceEquals(component.SceneNode, OwnerNodes[index]) ||
                 component.SceneNode.IsDestroyed || component.SceneNode.IsDestroyQueued ||
                 !component.SceneNode.Components.Contains(component)) ||
                !SourceAccessor.IsCurrent(owner, List))
                return false;
        }
        return true;
    }
}
