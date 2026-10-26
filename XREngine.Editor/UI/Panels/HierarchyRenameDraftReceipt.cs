using XREngine.Rendering.UI;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Retains an ordinary rename draft until the same panel can restore its row.</summary>
internal sealed class HierarchyRenameDraftReceipt(UITextInputComponent input, SceneNode source,
    UIListTransform list, UIVirtualTreeProvider provider, UIBoundableTransform row,
    UIVirtualRowLease lease, ulong originalSession)
{
    public UITextInputComponent OriginalInput { get; } = input;
    public ObjectConstructionIdentity OriginalIdentity { get; } = ObjectConstructionIdentity.Capture(input);
    public SceneNode OriginalNode { get; } = input.SceneNode;
    public ObjectConstructionIdentity OriginalNodeIdentity { get; } = ObjectConstructionIdentity.Capture(input.SceneNode);
    public TransformBase OriginalTransform { get; } = input.SceneNode.Transform;
    public ObjectConstructionIdentity OriginalTransformIdentity { get; } = ObjectConstructionIdentity.Capture(input.SceneNode.Transform);
    public UITextComponent? OriginalDisplay { get; } = input.SceneNode.GetComponent<UITextComponent>();
    public ObjectConstructionIdentity? OriginalDisplayIdentity { get; } = input.SceneNode.GetComponent<UITextComponent>() is { } display
        ? ObjectConstructionIdentity.Capture(display) : null;
    public ulong OriginalDisplayIntent { get; } = input.SceneNode.GetComponent<UITextComponent>()?.TextWriteIntentRevision ?? 0;
    public UIListTransform OriginalList { get; } = list;
    public ObjectConstructionIdentity OriginalListIdentity { get; } = ObjectConstructionIdentity.Capture(list);
    public SceneNode OriginalListNode { get; } = list.SceneNode!;
    public ObjectConstructionIdentity OriginalListNodeIdentity { get; } = ObjectConstructionIdentity.Capture(list.SceneNode!);
    public TransformBase? OriginalListParent { get; } = list.Parent;
    public ObjectConstructionIdentity? OriginalListParentIdentity { get; } = list.Parent is { } listParent
        ? ObjectConstructionIdentity.Capture(listParent) : null;
    public UIVirtualTreeProvider OriginalProvider { get; } = provider;
    public ulong OriginalProviderIncarnation { get; } = list.VirtualProviderIncarnation;
    public UIBoundableTransform OriginalRow { get; } = row;
    public ObjectConstructionIdentity OriginalRowIdentity { get; } = ObjectConstructionIdentity.Capture(row);
    public SceneNode OriginalRowNode { get; } = row.SceneNode!;
    public ObjectConstructionIdentity OriginalRowNodeIdentity { get; } = ObjectConstructionIdentity.Capture(row.SceneNode!);
    public UIVirtualRowLease OriginalLease { get; } = lease;
    public ulong OriginalSession { get; } = originalSession;
    public IRuntimeWorldContext? OriginalInputWorld { get; } = input.World;
    public IRuntimeWorldContext? OriginalNodeWorld { get; } = input.SceneNode.World;
    public IRuntimeWorldContext? OriginalTransformWorld { get; } = input.SceneNode.Transform.World;
    public IRuntimeWorldContext? OriginalDisplayWorld { get; } = input.SceneNode.GetComponent<UITextComponent>()?.World;
    public string Text { get; } = input.Text;
    public int Cursor { get; } = input.CursorPosition;
    public int Anchor { get; } = input.SelectionAnchor;
    public ulong TextIntent { get; } = input.TextWriteIntentRevision;
    public ulong CaretIntent { get; } = input.CaretWriteIntentRevision;
    public string? SourceName { get; } = source.Name;
    public TransformBase? SourceParent { get; } = source.Transform.Parent;
    public ObjectConstructionIdentity? SourceParentIdentity { get; } = source.Transform.Parent is { } parent
        ? ObjectConstructionIdentity.Capture(parent) : null;
    public bool ReleaseStarted { get; set; }
    public bool CaptureSettled { get; set; }
    public bool Prepared { get; set; }
    public ulong Session { get; set; }
    public UITextInputComponent? ReplacementInput { get; set; }
    public ObjectConstructionIdentity? ReplacementIdentity { get; private set; }
    public ObjectConstructionIdentity? ReplacementNodeIdentity { get; private set; }
    public ObjectConstructionIdentity? ReplacementTransformIdentity { get; private set; }
    public ObjectConstructionIdentity? ReplacementDisplayIdentity { get; private set; }
    public ulong AcceptedTextIntent { get; set; }
    public ulong AcceptedCaretIntent { get; set; }
    public UITextComponent? AcceptedDisplay { get; set; }
    public ulong AcceptedDisplayIntent { get; set; }

    private bool OriginalOwnerCurrent => !OriginalInput.IsDestroyed && !OriginalInput.IsDestroyQueued &&
        OriginalIdentity.IsCurrent && OriginalNodeIdentity.IsCurrent && OriginalTransformIdentity.IsCurrent &&
        !OriginalNode.IsDestroyed && !OriginalNode.IsDestroyQueued && !OriginalNode.IsTransformNull &&
        !OriginalTransform.IsDestroyed && !OriginalTransform.IsDestroyQueued &&
        ReferenceEquals(OriginalInput.SceneNode, OriginalNode) &&
        ReferenceEquals(OriginalNode.Transform, OriginalTransform) && OriginalNode.Components.Contains(OriginalInput) &&
        ReferenceEquals(OriginalTransform.Parent, OriginalRow) &&
        ReferenceEquals(OriginalInput.World, OriginalInputWorld) &&
        ReferenceEquals(OriginalNode.World, OriginalNodeWorld) &&
        ReferenceEquals(OriginalTransform.World, OriginalTransformWorld) &&
        OriginalDisplay is { IsDestroyed: false, IsDestroyQueued: false } &&
        OriginalDisplayIdentity?.IsCurrent == true && ReferenceEquals(OriginalDisplay.SceneNode, OriginalNode) &&
        OriginalNode.Components.Contains(OriginalDisplay) &&
        ReferenceEquals(OriginalDisplay.World, OriginalDisplayWorld) &&
        OriginalListIdentity.IsCurrent && OriginalListNodeIdentity.IsCurrent &&
        !OriginalList.IsDestroyed && !OriginalList.IsDestroyQueued &&
        !OriginalListNode.IsDestroyed && !OriginalListNode.IsDestroyQueued && !OriginalListNode.IsTransformNull &&
        ReferenceEquals(OriginalList.SceneNode, OriginalListNode) &&
        ReferenceEquals(OriginalListNode.Transform, OriginalList) &&
        ReferenceEquals(OriginalList.Parent, OriginalListParent) &&
        (OriginalListParentIdentity is not { } listParent || listParent.IsCurrent &&
            !listParent.Target.IsDestroyed && !listParent.Target.IsDestroyQueued) &&
        OriginalRowIdentity.IsCurrent && OriginalRowNodeIdentity.IsCurrent &&
        !OriginalRow.IsDestroyed && !OriginalRow.IsDestroyQueued &&
        !OriginalRowNode.IsDestroyed && !OriginalRowNode.IsDestroyQueued && !OriginalRowNode.IsTransformNull &&
        ReferenceEquals(OriginalRow.SceneNode, OriginalRowNode) &&
        ReferenceEquals(OriginalRowNode.Transform, OriginalRow) &&
        ReferenceEquals(OriginalRow.Parent, OriginalList);

    public bool OriginalBindingCurrent => OriginalOwnerCurrent &&
        ReferenceEquals(OriginalList.VirtualProvider, OriginalProvider) &&
        OriginalList.VirtualProviderIncarnation == OriginalProviderIncarnation &&
        OriginalList.TryGetRealizedVirtualRow(OriginalLease.ItemId,
            out UIBoundableTransform? row, out UIVirtualRowLease lease) &&
        ReferenceEquals(row, OriginalRow) && lease == OriginalLease;

    private bool OriginalTextCurrent => !OriginalInput.CommitOnSubmit &&
        OriginalInput.TextWriteIntentRevision == TextIntent &&
        OriginalInput.Text == Text && !OriginalInput.IsComposing &&
        OriginalDisplay!.TextWriteIntentRevision == OriginalDisplayIntent;

    public bool OriginalDraftCurrent => OriginalOwnerCurrent && OriginalTextCurrent && OriginalCaretCurrent;

    private bool OriginalProviderRetired => OriginalList.VirtualProvider is null &&
        OriginalList.VirtualProviderIncarnation == unchecked(OriginalProviderIncarnation + 1);

    private bool OriginalCaretCurrent => OriginalInput.CaretWriteIntentRevision == CaretIntent &&
        OriginalInput.CursorPosition == Cursor && OriginalInput.SelectionAnchor == Anchor;

    // Terminal row cleanup writes the anchor once, including an equal-value write.
    // Any additional caret intent cancels this transient restore.
    private bool OriginalRecycledCaretCurrent => CaretIntent != ulong.MaxValue &&
        OriginalInput.CaretWriteIntentRevision == CaretIntent + 1 &&
        OriginalInput.CursorPosition == Cursor && OriginalInput.SelectionAnchor == Cursor;

    public bool OriginalReleaseCurrent => OriginalOwnerCurrent && OriginalTextCurrent &&
        (OriginalBindingCurrent || OriginalProviderRetired) &&
        (OriginalCaretCurrent || OriginalRecycledCaretCurrent);

    public bool OriginalReleaseComplete => ReleaseStarted && OriginalOwnerCurrent &&
        OriginalTextCurrent && OriginalProviderRetired && OriginalRecycledCaretCurrent;

    public bool ParentCurrent => SourceParentIdentity is not { } identity ||
        identity.IsCurrent && !identity.Target.IsDestroyed && !identity.Target.IsDestroyQueued;

    public void CaptureDestination(UITextInputComponent input)
    {
        if (ReplacementInput is not null || input.SceneNode is not { IsTransformNull: false } node ||
            node.GetComponent<UITextComponent>() is not { } display)
            return;
        ReplacementInput = input;
        ReplacementIdentity = ObjectConstructionIdentity.Capture(input);
        ReplacementNodeIdentity = ObjectConstructionIdentity.Capture(node);
        ReplacementTransformIdentity = ObjectConstructionIdentity.Capture(node.Transform);
        ReplacementDisplayIdentity = ObjectConstructionIdentity.Capture(display);
        AcceptedTextIntent = input.TextWriteIntentRevision;
        AcceptedCaretIntent = input.CaretWriteIntentRevision;
        AcceptedDisplay = display;
        AcceptedDisplayIntent = display.TextWriteIntentRevision;
    }

    public bool DestinationOwnerCurrent => ReplacementInput is { IsDestroyed: false, IsDestroyQueued: false } input &&
        ReplacementIdentity?.IsCurrent == true && ReplacementNodeIdentity is { IsCurrent: true } nodeIdentity &&
        ReplacementTransformIdentity is { IsCurrent: true } transformIdentity && ReplacementDisplayIdentity?.IsCurrent == true &&
        input.SceneNode is { IsDestroyed: false, IsDestroyQueued: false, IsTransformNull: false } node &&
        ReferenceEquals(node, nodeIdentity.Target) &&
        ReferenceEquals(node.Transform, transformIdentity.Target) &&
        !node.Transform.IsDestroyed && !node.Transform.IsDestroyQueued && node.Components.Contains(input) &&
        AcceptedDisplay is { IsDestroyed: false, IsDestroyQueued: false } display &&
        ReferenceEquals(display.SceneNode, node) && node.Components.Contains(display);

    public bool DestinationCurrent => DestinationOwnerCurrent &&
        ReplacementInput!.TextWriteIntentRevision == AcceptedTextIntent &&
        ReplacementInput.CaretWriteIntentRevision == AcceptedCaretIntent &&
        AcceptedDisplay!.TextWriteIntentRevision == AcceptedDisplayIntent;
}
