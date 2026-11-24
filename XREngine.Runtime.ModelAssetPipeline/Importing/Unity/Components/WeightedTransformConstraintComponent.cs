using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Weighted position, rotation, scale, parent, and aim constraint used by imported
/// avatars. Each frame after animation it reads its source transforms, computes a pose for its target
/// transform, and writes that pose on the affected channels.
/// </summary>
/// <remarks>
/// One component covers all native and avatar SDK constraint types through <see cref="Mode"/>, in the same
/// way that the avatar SDK runtime solves every type with one job: position, rotation, and scale
/// constraints use <see cref="TransformConstraintMode.Match"/>, parent constraints use
/// <see cref="TransformConstraintMode.Parent"/>, and aim and look-at constraints use
/// <see cref="TransformConstraintMode.Aim"/>.
/// </remarks>
[Serializable]
public sealed partial class WeightedTransformConstraintComponent : XRComponent
{
    /// <summary>
    /// The largest <see cref="ExecutionOrder"/>. The avatar SDK runtime limits constraint chains to a depth
    /// of 512, so this value leaves room and keeps the tick order inside the animation order range.
    /// </summary>
    public const int MaxExecutionOrder = 1024;

    private TransformBase? _targetTransform;
    private List<TransformConstraintSource> _sources = [];
    private TransformConstraintMode _mode = TransformConstraintMode.Match;
    private TransformConstraintChannels _channels = TransformConstraintChannels.Parent;
    private float _weight = 1.0f;
    private bool _solveInLocalSpace;
    private bool _locked;
    private Vector3 _positionOffset;
    private Quaternion _rotationOffset = Quaternion.Identity;
    private Vector3 _scaleOffset = Vector3.One;
    private bool _hasRestPose;
    private Vector3 _positionAtRest;
    private Quaternion _rotationAtRest = Quaternion.Identity;
    private Vector3 _scaleAtRest = Vector3.One;
    private bool _freezeToWorld;
    private bool _rebakeOffsetsWhenUnfrozen;
    private Vector3 _aimAxis = -Vector3.UnitZ;
    private Vector3 _upAxis = Vector3.UnitY;
    private TransformConstraintWorldUpType _worldUpType = TransformConstraintWorldUpType.SceneUp;
    private Vector3 _worldUpVector = Vector3.UnitY;
    private TransformBase? _worldUpTransform;
    private float _roll;
    private int _executionOrder;
    private bool _hasDependents;

    private bool _frozenPoseCaptured;
    private Vector3 _frozenWorldPosition;
    private Quaternion _frozenWorldRotation = Quaternion.Identity;
    private Vector3 _frozenWorldScale = Vector3.One;
    private bool _rebakePending;
    private bool _tickRegistered;
    private int _registeredTickOrder;
    private TransformBase? _registeredConstraintTarget;
    private TransformConstraintChannels _registeredConstraintChannels;

    /// <summary>The transform that the constraint moves. When null, the owning node's transform is used.</summary>
    public TransformBase? TargetTransform
    {
        get => _targetTransform;
        set
        {
            if (SetField(ref _targetTransform, value))
                RefreshFullConstraintOwnership();
        }
    }

    public List<TransformConstraintSource> Sources
    {
        get => _sources;
        set
        {
            if (SetField(ref _sources, value ?? []))
                RefreshFullConstraintOwnership();
        }
    }

    /// <summary>Selects how the sources become a target pose. See <see cref="TransformConstraintMode"/>.</summary>
    public TransformConstraintMode Mode
    {
        get => _mode;
        set => SetField(ref _mode, value);
    }

    /// <summary>
    /// The target channels that the constraint writes. Unaffected channels keep the value that animation
    /// or other code gave them this frame. <see cref="TransformConstraintMode.Parent"/> writes no scale, and
    /// <see cref="TransformConstraintMode.Aim"/> writes only rotation, whatever this value holds.
    /// </summary>
    public TransformConstraintChannels Channels
    {
        get => _channels;
        set
        {
            if (SetField(ref _channels, value))
                RefreshFullConstraintOwnership();
        }
    }

    public float Weight
    {
        get => _weight;
        set
        {
            if (SetField(ref _weight, Math.Clamp(value, 0.0f, 1.0f)))
                RefreshFullConstraintOwnership();
        }
    }

    /// <summary>
    /// True when the constraint reads the local values of its sources and writes the local values of the
    /// target. False when it reads and writes world values.
    /// </summary>
    public bool SolveInLocalSpace
    {
        get => _solveInLocalSpace;
        set => SetField(ref _solveInLocalSpace, value);
    }

    /// <summary>
    /// True when the authored at-rest and offset values are fixed, so the constraint drives the
    /// target. A source constraint that is not locked recalculates its at-rest and offset values
    /// from the current pose instead of moving the target, so this component does not drive it.
    /// </summary>
    public bool Locked
    {
        get => _locked;
        set
        {
            if (SetField(ref _locked, value))
                RefreshFullConstraintOwnership();
        }
    }

    /// <summary>
    /// Constraint-level position offset that is added to the weighted source position. It is in the
    /// target's parent space: a world-space solve moves it by the parent's world matrix (rotation and
    /// scale). The avatar SDK bakes it so that the target holds <see cref="PositionAtRest"/> when the sources
    /// are at rest (rest = source position in the parent space + offset).
    /// </summary>
    public Vector3 PositionOffset
    {
        get => _positionOffset;
        set => SetField(ref _positionOffset, value);
    }

    /// <summary>
    /// Constraint-level rotation offset, applied after the solved rotation (solved rotation * offset).
    /// </summary>
    public Quaternion RotationOffset
    {
        get => _rotationOffset;
        set => SetField(ref _rotationOffset, value);
    }

    /// <summary>
    /// Constraint-level scale offset. The constraint multiplies the weighted source scale by it, per axis.
    /// </summary>
    public Vector3 ScaleOffset
    {
        get => _scaleOffset;
        set => SetField(ref _scaleOffset, value);
    }

    /// <summary>
    /// True when <see cref="PositionAtRest"/>, <see cref="RotationAtRest"/>, and <see cref="ScaleAtRest"/>
    /// hold authored values. The constraint then blends from the rest pose by <see cref="Weight"/>, and
    /// uses the rest pose when the total source weight is zero.
    /// </summary>
    public bool HasRestPose
    {
        get => _hasRestPose;
        set
        {
            if (SetField(ref _hasRestPose, value))
                RefreshFullConstraintOwnership();
        }
    }

    /// <summary>The target's local position when the constraint weight or the total source weight is zero.</summary>
    public Vector3 PositionAtRest
    {
        get => _positionAtRest;
        set => SetField(ref _positionAtRest, value);
    }

    /// <summary>The target's local rotation when the constraint weight or the total source weight is zero.</summary>
    public Quaternion RotationAtRest
    {
        get => _rotationAtRest;
        set => SetField(ref _rotationAtRest, value);
    }

    /// <summary>The target's local scale when the constraint weight or the total source weight is zero.</summary>
    public Vector3 ScaleAtRest
    {
        get => _scaleAtRest;
        set => SetField(ref _scaleAtRest, value);
    }

    /// <summary>
    /// Freeze To World: the constraint ignores its sources and holds the target's world
    /// position, rotation, and scale, on the affected channels, from the first solve after activation.
    /// </summary>
    public bool FreezeToWorld
    {
        get => _freezeToWorld;
        set
        {
            bool wasFrozen = _freezeToWorld;
            if (!SetField(ref _freezeToWorld, value))
                return;

            if (value)
                _frozenPoseCaptured = false;
            else if (wasFrozen && RebakeOffsetsWhenUnfrozen)
                _rebakePending = true;

            RefreshFullConstraintOwnership();
        }
    }

    /// <summary>
    /// Rebake Offsets When Unfrozen: when <see cref="FreezeToWorld"/> changes from true to false,
    /// the next solve recalculates the offsets so that the target stays at the pose that it held while
    /// frozen, instead of returning to the authored offsets.
    /// </summary>
    public bool RebakeOffsetsWhenUnfrozen
    {
        get => _rebakeOffsetsWhenUnfrozen;
        set => SetField(ref _rebakeOffsetsWhenUnfrozen, value);
    }

    /// <summary>
    /// <see cref="TransformConstraintMode.Aim"/>: the target's local axis that points at the weighted
    /// source position. The default is the engine forward axis (-Z).
    /// </summary>
    public Vector3 AimAxis
    {
        get => _aimAxis;
        set => SetField(ref _aimAxis, value);
    }

    /// <summary>
    /// <see cref="TransformConstraintMode.Aim"/>: the target's local axis that the constraint turns toward
    /// the world-up direction, as far as the aim allows.
    /// </summary>
    public Vector3 UpAxis
    {
        get => _upAxis;
        set => SetField(ref _upAxis, value);
    }

    /// <summary><see cref="TransformConstraintMode.Aim"/>: selects the world-up direction.</summary>
    public TransformConstraintWorldUpType WorldUpType
    {
        get => _worldUpType;
        set => SetField(ref _worldUpType, value);
    }

    /// <summary>
    /// <see cref="TransformConstraintMode.Aim"/>: the up vector for
    /// <see cref="TransformConstraintWorldUpType.Vector"/>, and the vector that the world-up transform
    /// rotates for <see cref="TransformConstraintWorldUpType.ObjectRotationUp"/>.
    /// </summary>
    public Vector3 WorldUpVector
    {
        get => _worldUpVector;
        set => SetField(ref _worldUpVector, value);
    }

    /// <summary>
    /// <see cref="TransformConstraintMode.Aim"/>: the transform for
    /// <see cref="TransformConstraintWorldUpType.ObjectUp"/> and
    /// <see cref="TransformConstraintWorldUpType.ObjectRotationUp"/>. When it is null, those types use the
    /// scene up direction and <see cref="WorldUpVector"/>.
    /// </summary>
    public TransformBase? WorldUpTransform
    {
        get => _worldUpTransform;
        set => SetField(ref _worldUpTransform, value);
    }

    /// <summary>
    /// <see cref="TransformConstraintMode.Aim"/>: degrees of rotation around <see cref="AimAxis"/> that the
    /// constraint applies after the aim (right-handed, in engine coordinates).
    /// </summary>
    public float Roll
    {
        get => _roll;
        set => SetField(ref _roll, value);
    }

    /// <summary>
    /// The position of this constraint in the constraint evaluation order. A constraint with a larger
    /// value solves later in the same frame, so a constraint that reads a transform that another
    /// constraint moves must have a larger value than that constraint.
    /// <see cref="TransformConstraintExecutionOrder.Assign"/> calculates it from the constraint dependencies.
    /// </summary>
    public int ExecutionOrder
    {
        get => _executionOrder;
        set => SetField(ref _executionOrder, Math.Clamp(value, 0, MaxExecutionOrder));
    }

    /// <summary>
    /// True when another constraint reads a transform that this constraint moves. The constraint then
    /// recalculates the world matrices of its target subtree right after it writes the target, so that
    /// later constraints in the same frame read the new pose.
    /// </summary>
    public bool HasDependents
    {
        get => _hasDependents;
        set => SetField(ref _hasDependents, value);
    }

    private static int GetTickOrder(int executionOrder)
        => (int)ETickOrder.Animation + 1 + executionOrder;

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        if (propName == nameof(ExecutionOrder) && _tickRegistered)
            RegisterConstraintTick();
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        _frozenPoseCaptured = false;
        _rebakePending = false;
        RefreshFullConstraintOwnership();
        RegisterConstraintTick();
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterConstraintTick();
        ReleaseFullConstraintOwnership();
        base.OnComponentDeactivated();
    }

    private TransformConstraintChannels ResolveDrivenOwnershipChannels()
    {
        if (!IsActiveInHierarchy || !Locked)
            return TransformConstraintChannels.None;

        TransformConstraintChannels owned = TransformConstraintChannels.None;
        if ((Channels & TransformConstraintChannels.Position) == TransformConstraintChannels.Position)
            owned |= TransformConstraintChannels.Position;
        if ((Channels & TransformConstraintChannels.Rotation) == TransformConstraintChannels.Rotation)
            owned |= TransformConstraintChannels.Rotation;
        if (owned == TransformConstraintChannels.None)
            return TransformConstraintChannels.None;

        if (FreezeToWorld)
            return owned;

        if (Weight < 1.0f - 1.0e-4f)
            return TransformConstraintChannels.None;

        if (HasRestPose)
            return owned;

        for (int i = 0; i < _sources.Count; ++i)
            if (_sources[i].SourceTransform is not null && _sources[i].Weight > 0.0f)
                return owned;

        return TransformConstraintChannels.None;
    }

    private void RefreshFullConstraintOwnership()
    {
        TransformConstraintChannels desiredChannels = ResolveDrivenOwnershipChannels();
        TransformBase? desiredTarget = desiredChannels != TransformConstraintChannels.None
            ? (_targetTransform ?? Transform)
            : null;
        if (ReferenceEquals(_registeredConstraintTarget, desiredTarget)
            && _registeredConstraintChannels == desiredChannels)
            return;

        ReleaseFullConstraintOwnership();
        if (desiredTarget is null)
            return;

        if ((desiredChannels & TransformConstraintChannels.Position) == TransformConstraintChannels.Position)
            desiredTarget.AddPositionConstraintOwner();
        if ((desiredChannels & TransformConstraintChannels.Rotation) == TransformConstraintChannels.Rotation)
            desiredTarget.AddRotationConstraintOwner();

        _registeredConstraintTarget = desiredTarget;
        _registeredConstraintChannels = desiredChannels;
    }

    private void ReleaseFullConstraintOwnership()
    {
        if (_registeredConstraintTarget is null)
            return;

        if ((_registeredConstraintChannels & TransformConstraintChannels.Position) == TransformConstraintChannels.Position)
            _registeredConstraintTarget.RemovePositionConstraintOwner();
        if ((_registeredConstraintChannels & TransformConstraintChannels.Rotation) == TransformConstraintChannels.Rotation)
            _registeredConstraintTarget.RemoveRotationConstraintOwner();

        _registeredConstraintTarget = null;
        _registeredConstraintChannels = TransformConstraintChannels.None;
    }

    /// <summary>
    /// Registers the solve in the late tick group, after animation and the physics chains. The order
    /// inside that group comes from <see cref="ExecutionOrder"/>.
    /// </summary>
    private void RegisterConstraintTick()
    {
        UnregisterConstraintTick();
        _registeredTickOrder = GetTickOrder(ExecutionOrder);
        RegisterTick(ETickGroup.Late, _registeredTickOrder, Evaluate);
        _tickRegistered = true;
    }

    private void UnregisterConstraintTick()
    {
        if (!_tickRegistered)
            return;

        UnregisterTick(ETickGroup.Late, _registeredTickOrder, Evaluate);
        _tickRegistered = false;
    }
}
