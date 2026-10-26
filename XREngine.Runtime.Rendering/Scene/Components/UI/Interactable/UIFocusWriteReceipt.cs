using XREngine.Data.Core;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI;

/// <summary>Retains the original owner and progress of one guarded focus write.</summary>
public sealed class UIFocusWriteReceipt
{
    private readonly ObjectConstructionIdentity _targetIdentity;
    private readonly SceneNode _node;
    private readonly ObjectConstructionIdentity _nodeIdentity;
    private readonly TransformBase _transform;
    private readonly ObjectConstructionIdentity _transformIdentity;
    private readonly TransformBase? _parent;
    private readonly ObjectConstructionIdentity? _parentIdentity;
    private readonly object? _world;
    private readonly object? _targetWorld;
    private readonly object? _transformWorld;
    private readonly UICanvasTransform? _canvas;
    private readonly ObjectConstructionIdentity? _canvasIdentity;
    private ulong _expectedAttempt;

    internal UIFocusWriteReceipt(UIInteractableComponent target, ulong attempt)
    {
        Target = target;
        _targetIdentity = ObjectConstructionIdentity.Capture(target);
        _node = target.SceneNode;
        _nodeIdentity = ObjectConstructionIdentity.Capture(_node);
        _transform = _node.Transform;
        _transformIdentity = ObjectConstructionIdentity.Capture(_transform);
        _parent = _transform.Parent;
        _parentIdentity = _parent is null ? null : ObjectConstructionIdentity.Capture(_parent);
        _world = _node.World;
        _targetWorld = target.World;
        _transformWorld = _transform.World;
        _canvas = (_transform as UITransform)?.ParentCanvas;
        _canvasIdentity = _canvas is null ? null : ObjectConstructionIdentity.Capture(_canvas);
        _expectedAttempt = attempt;
        ImeIdentity = (_transform as UITransform)?.ElementIdentity ?? default;
        AcquiredTextFocusEpoch = target is UITextInputComponent text ? text.FocusEpoch : 0;
    }

    public UIInteractableComponent Target { get; }
    public bool GainWasWritten { get; internal set; }
    public bool AbortCompleted { get; internal set; }
    public bool RetainsFocusAfterVeto => AbortVetoed && OwnsFocus;
    public bool OwnsFocus => (GainWasWritten || IsPreservingRelease) && OwnerCurrent && Target.IsFocused;
    public UIElementIdentity ImeIdentity { get; }
    public long AcquiredTextFocusEpoch { get; internal set; }
    public long AbortedTextFocusEpoch { get; internal set; }
    internal bool AbortStarted { get; set; }
    internal bool AbortLossStarted { get; set; }
    internal bool AbortVetoed { get; set; }
    internal bool LostFocusRaised { get; set; }
    internal Exception? CleanupFailure { get; set; }
    public bool HasCurrentOwner => OwnerCurrent;
    internal Action? RetireHostInput { get; set; }

    public bool HasNewerFocusIntent => OriginalOwnerCurrent &&
        Target.FocusWriteIntentRevision != _expectedAttempt;
    public bool IsCurrent => OwnerCurrent && (ContentAdmission?.Invoke() ?? true) && OwnerCurrent;
    internal Func<bool>? ContentAdmission { get; set; }
    internal Action? AdvanceContentEpoch { get; set; }
    internal bool IsPreservingRelease { get; set; }
    internal bool OwnerCurrent => OriginalOwnerCurrent && Target.FocusWriteIntentRevision == _expectedAttempt;

    private bool OriginalOwnerCurrent => _targetIdentity.IsCurrent && _nodeIdentity.IsCurrent &&
        _transformIdentity.IsCurrent && !Target.IsDestroyed && !Target.IsDestroyQueued &&
        !_node.IsDestroyed && !_node.IsDestroyQueued && !_node.IsTransformNull &&
        !_transform.IsDestroyed && !_transform.IsDestroyQueued &&
        ReferenceEquals(Target.SceneNode, _node) && ReferenceEquals(_node.Transform, _transform) &&
        UITextWriteOwner.Contains(_node, Target) && ReferenceEquals(_transform.Parent, _parent) &&
        ReferenceEquals(_node.World, _world) && ReferenceEquals(Target.World, _targetWorld) &&
        ReferenceEquals(_transform.World, _transformWorld) &&
        (_parentIdentity is not { } parentIdentity || parentIdentity.IsCurrent &&
            !parentIdentity.Target.IsDestroyed && !parentIdentity.Target.IsDestroyQueued) &&
        (_canvasIdentity is not { } canvasIdentity || canvasIdentity.IsCurrent &&
            !canvasIdentity.Target.IsDestroyed && !canvasIdentity.Target.IsDestroyQueued) &&
        ReferenceEquals((_transform as UITransform)?.ParentCanvas, _canvas);

    internal void AdvanceAttempt(ulong attempt) => _expectedAttempt = attempt;
}
