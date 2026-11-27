using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Components.VR;
using XREngine.Runtime.Bootstrap;
using XREngine.Runtime.Bootstrap.Builders;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>
/// Temporarily lends one desktop avatar to an editor-owned VR pawn. The avatar stays
/// user-owned and returns to its original hierarchy before the VR pawn is destroyed.
/// </summary>
internal sealed class EditorVrAvatarLease : IDisposable
{
    private readonly SceneNode _avatar;
    private readonly HumanoidComponent _humanoid;
    private readonly EHumanoidPosePreviewMode _originalPosePreviewMode;
    private readonly SceneNode _character;
    private readonly TransformBase? _originalParent;
    private readonly Matrix4x4 _originalLocalMatrix;
    private readonly Quaternion _preVrWorldYaw;
    private readonly RuntimeWorld _world;
    private readonly bool _wasRuntimeRoot;
    private readonly List<XRComponent> _pausedWriters = [];
    private readonly List<bool> _writerActive = [];
    private readonly List<bool> _writerSuspended = [];
    private readonly List<bool> _resumeClips = [];
    private int _pausedWriterCount;
    private CharacterRootMotionBridgeComponent? _pausedRootMotionBridge;
    private bool _rootMotionBridgeWasActive;
    private VRHeightScaleComponent? _vrScale;
    private VRIKSolverComponent? _vrSolver;
    private VRPlayerCharacterComponent? _player;
    private bool _disposed;

    private EditorVrAvatarLease(SceneNode avatar, HumanoidComponent humanoid, SceneNode character, RuntimeWorld world)
    {
        _avatar = avatar;
        _humanoid = humanoid;
        _originalPosePreviewMode = humanoid.PosePreviewMode;
        _character = character;
        _originalParent = avatar.Transform.Parent;
        _originalLocalMatrix = avatar.Transform.LocalMatrix;
        _preVrWorldYaw = ExtractPlanarYawRotation(avatar.Transform.WorldRotation);
        _world = world;
        _wasRuntimeRoot = _originalParent is null && world.RootNodes.Any(root => ReferenceEquals(root, avatar));
        avatar.IterateComponents<XRComponent>(component =>
        {
            if (component is AnimStateMachineComponent stateMachine && !stateMachine.DrivesHumanoidBodyPose)
                return;
            if (component is not (HeightScaleComponent or BaseIKSolverComponent or
                AnimStateMachineComponent or AnimationClipComponent or PhysicsChainComponent or
                MotionBricksDebugDrawComponent))
                return;
            _pausedWriters.Add(component);
            _writerActive.Add(component.IsActive);
            _writerSuspended.Add(component is AnimStateMachineComponent machine && machine.SuspendedByClip);
            _resumeClips.Add(component is AnimationClipComponent clip && clip.IsPlaying && !clip.IsPaused);
        }, true);

        for (SceneNode? ancestor = _originalParent?.SceneNode; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.GetComponent<CharacterRootMotionBridgeComponent>() is { } bridge
                && (ReferenceEquals(bridge.AvatarFacingTransform?.SceneNode, avatar)
                    || ReferenceEquals(bridge.Animator?.SceneNode, avatar)))
            {
                _pausedRootMotionBridge = bridge;
                _rootMotionBridgeWasActive = bridge.IsActive;
                break;
            }
        }
    }

    public static bool TryAttach(
        SceneNode avatar,
        SceneNode footNode,
        SceneNode character,
        SceneNode playspace,
        RuntimeWorld world,
        out EditorVrAvatarLease? lease,
        out string? diagnostic)
    {
        lease = null;
        diagnostic = null;
        HumanoidComponent? humanoid = avatar.GetComponent<HumanoidComponent>();
        if (humanoid is null || avatar.GetComponent<VRHeightScaleComponent>() is not null ||
            avatar.GetComponent<VRIKSolverComponent>() is not null)
        {
            diagnostic = "The selected desktop avatar already has a VR scale or solver owner.";
            return false;
        }

        var candidate = new EditorVrAvatarLease(avatar, humanoid, character, world);
        try
        {
            Vector3 preVrFootWorldPosition = ResolveDesktopFootWorldPosition(avatar, candidate._originalParent);
            PositionVrCharacterAtAvatar(character, footNode, preVrFootWorldPosition, candidate._preVrWorldYaw);

            if (candidate._pausedRootMotionBridge is { IsDestroyed: false } bridge && candidate._rootMotionBridgeWasActive)
                bridge.IsActive = false;

            for (int i = 0; i < candidate._pausedWriters.Count; i++)
            {
                XRComponent writer = candidate._pausedWriters[i];
                candidate._pausedWriterCount = i + 1;
                if (writer is AnimationClipComponent clip)
                    clip.Pause();
                else if (writer is AnimStateMachineComponent machine)
                    machine.SetSuspendedByClip(true);
                else
                    writer.IsActive = false;
            }

            if (candidate._wasRuntimeRoot)
                world.RootNodes.Remove(avatar);
            avatar.Transform.SetParent(footNode.Transform, false, EParentAssignmentMode.Immediate);
            avatar.Transform.DeriveLocalMatrix(Matrix4x4.Identity);
            candidate._vrScale = avatar.AddComponent<VRHeightScaleComponent>()
                ?? throw new InvalidOperationException("Could not create the VR avatar scale owner.");
            candidate._vrSolver = avatar.AddComponent<VRIKSolverComponent>()
                ?? throw new InvalidOperationException("Could not create the VR avatar solver.");
            candidate._vrSolver.IsActive = false;
            candidate._player = character.GetComponent<VRPlayerCharacterComponent>();
            candidate._player = BootstrapPawnFactory.BindVrAvatar(
                avatar, character, playspace, humanoid, candidate._vrScale, candidate._vrSolver);
            if (candidate._player is not null)
                candidate._player.AutoBeginCalibrationWhenUncached = true;
            lease = candidate;
            return true;
        }
        catch (Exception ex)
        {
            candidate.Dispose();
            diagnostic = $"Could not attach the desktop avatar to VR: {ex.Message}";
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        bool hasPostVrPose = TryCaptureVrAvatarFootPose(out Vector3 postVrFootWorldPosition, out Quaternion postVrWorldYaw);

        if (_player is { IsDestroyed: false, IsCalibrating: true })
            _player.CancelCalibrationNow();
        if (_vrSolver is { IsDestroyed: false })
            _vrSolver.EndCalibrationPose();

        if (_player is not null)
            _player.IsActive = false;
        if (_character.GetComponent<VrLocalAvatarVisibilityComponent>() is { } visibility)
            visibility.IsActive = false;
        if (_character.GetComponent<VrCalibrationFeedbackComponent>() is { } feedback)
            feedback.IsActive = false;
        if (_character.FindDescendantByName("VR Calibration Feedback")?.GetComponent<VrCalibrationFeedbackComponent>() is { } childFeedback)
            childFeedback.IsActive = false;

        if (_vrSolver is not null && ReferenceEquals(_avatar.GetComponent<VRIKSolverComponent>(), _vrSolver))
            _avatar.RemoveComponent<VRIKSolverComponent>();
        if (_vrScale is not null && ReferenceEquals(_avatar.GetComponent<VRHeightScaleComponent>(), _vrScale))
            _avatar.RemoveComponent<VRHeightScaleComponent>();

        if (!_humanoid.IsDestroyed)
        {
            _humanoid.ClearIKTargets();
            _humanoid.PosePreviewMode = _originalPosePreviewMode;
        }

        if (!_avatar.IsDestroyed)
        {
            TransformBase? restoreParent = _originalParent?.SceneNode is { IsDestroyed: false }
                ? _originalParent
                : null;
            _avatar.Transform.SetParent(restoreParent, false, EParentAssignmentMode.Immediate);
            _avatar.Transform.DeriveLocalMatrix(_originalLocalMatrix);
            if (_wasRuntimeRoot && restoreParent is null &&
                !_world.RootNodes.Any(root => ReferenceEquals(root, _avatar)))
                _world.RootNodes.Add(_avatar);

            if (hasPostVrPose)
                ApplyPostVrPoseToDesktopOwner(restoreParent, postVrFootWorldPosition, postVrWorldYaw);
        }

        for (int i = 0; i < _pausedWriterCount; i++)
            if (_pausedWriters[i] is { IsDestroyed: false } writer)
            {
                if (writer is AnimationClipComponent clip)
                {
                    if (_resumeClips[i]) clip.Resume();
                }
                else if (writer is AnimStateMachineComponent machine)
                    machine.SetSuspendedByClip(_writerSuspended[i]);
                else
                    writer.IsActive = _writerActive[i];
            }

        if (_pausedRootMotionBridge is { IsDestroyed: false } bridge && _rootMotionBridgeWasActive)
            bridge.IsActive = true;
    }

    private static void PositionVrCharacterAtAvatar(
        SceneNode character,
        SceneNode footNode,
        Vector3 avatarFootWorldPosition,
        Quaternion avatarWorldYaw)
    {
        if (!IsFinite(avatarFootWorldPosition))
            return;

        if (character.GetComponent<CharacterMovement3DComponent>() is { } vrMovement)
        {
            Vector3 targetCapsulePosition = new(
                avatarFootWorldPosition.X,
                avatarFootWorldPosition.Y + vrMovement.HalfHeight + 0.01f,
                avatarFootWorldPosition.Z);
            vrMovement.Teleport(targetCapsulePosition, clearMotion: true);
        }
        else if (character.Transform is RigidBodyTransform vrRigidBody)
        {
            vrRigidBody.SetPositionAndRotation(avatarFootWorldPosition, vrRigidBody.Rotation);
        }
        else if (character.Transform is Transform vrTransform)
        {
            SetTransformWorldTranslation(vrTransform, avatarFootWorldPosition);
        }

        TransformBase? vrYawTransform = character.GetComponent<CharacterPawnComponent>()?.ViewRotationTransform
            ?? footNode.Parent?.Transform;
        if (vrYawTransform is Transform yawTransform && !ReferenceEquals(vrYawTransform, character.Transform))
            yawTransform.Rotation = avatarWorldYaw;
    }

    private static Vector3 ResolveDesktopFootWorldPosition(SceneNode avatar, TransformBase? originalParent)
    {
        Vector3 footPosition = avatar.Transform.WorldTranslation;
        for (SceneNode? ancestor = originalParent?.SceneNode; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.GetComponent<CharacterMovement3DComponent>() is not { } desktopMovement)
                continue;

            Vector3 capsuleCenter = ancestor.Transform.WorldTranslation;
            if (IsFinite(capsuleCenter))
                footPosition.Y = capsuleCenter.Y - desktopMovement.HalfHeight;
            break;
        }

        return footPosition;
    }

    private bool TryCaptureVrAvatarFootPose(out Vector3 footWorldPosition, out Quaternion worldYaw)
    {
        footWorldPosition = Vector3.Zero;
        worldYaw = Quaternion.Identity;
        if (_avatar.IsDestroyed)
            return false;

        Vector3 avatarFootPosition = _avatar.Transform.WorldTranslation;
        bool hasVrCapsuleFootY = false;
        float vrCapsuleFootY = 0.0f;
        if (!_character.IsDestroyed && _character.GetComponent<CharacterMovement3DComponent>() is { } vrMovement)
        {
            Vector3 capsuleCenter = _character.Transform.WorldTranslation;
            if (IsFinite(capsuleCenter))
            {
                // Use the VR character capsule bottom for Y so HMD height offsets during calibration
                // or crouching never push the restored desktop capsule below the floor.
                vrCapsuleFootY = capsuleCenter.Y - vrMovement.HalfHeight;
                hasVrCapsuleFootY = true;
                if (!IsFinite(avatarFootPosition))
                    avatarFootPosition = new Vector3(capsuleCenter.X, vrCapsuleFootY, capsuleCenter.Z);
            }
        }

        if (!IsFinite(avatarFootPosition))
            return false;

        // Include room-scale horizontal travel when the humanoid hips moved away from the foot anchor.
        if (!_humanoid.IsDestroyed && _humanoid.Hips.Node?.Transform is { } hipsTransform)
        {
            Vector3 hipsWorldPosition = hipsTransform.WorldTranslation;
            if (IsFinite(hipsWorldPosition))
            {
                float dx = hipsWorldPosition.X - avatarFootPosition.X;
                float dz = hipsWorldPosition.Z - avatarFootPosition.Z;
                if ((dx * dx + dz * dz) < 100.0f)
                {
                    avatarFootPosition.X = hipsWorldPosition.X;
                    avatarFootPosition.Z = hipsWorldPosition.Z;
                }
            }
        }

        if (hasVrCapsuleFootY)
            avatarFootPosition.Y = vrCapsuleFootY;

        footWorldPosition = avatarFootPosition;
        worldYaw = ExtractPlanarYawRotation(_avatar.Transform.WorldRotation);
        return true;
    }

    private void ApplyPostVrPoseToDesktopOwner(
        TransformBase? restoreParent,
        Vector3 postVrFootWorldPosition,
        Quaternion postVrWorldYaw)
    {
        if (!IsFinite(postVrFootWorldPosition))
            return;

        CharacterMovement3DComponent? desktopMovement = null;
        SceneNode? desktopCharacterNode = null;
        for (SceneNode? ancestor = restoreParent?.SceneNode; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.GetComponent<CharacterMovement3DComponent>() is { } movement)
            {
                desktopMovement = movement;
                desktopCharacterNode = ancestor;
                break;
            }
        }

        if (desktopMovement is not null && desktopCharacterNode is not null)
        {
            Vector3 targetCapsulePosition = new(
                postVrFootWorldPosition.X,
                postVrFootWorldPosition.Y + desktopMovement.HalfHeight + 0.01f,
                postVrFootWorldPosition.Z);
            desktopMovement.Teleport(targetCapsulePosition, clearMotion: true);

            Quaternion parentYaw = restoreParent is not null
                ? ExtractPlanarYawRotation(restoreParent.WorldRotation)
                : Quaternion.Identity;
            Quaternion localYaw = Quaternion.Normalize(Quaternion.Inverse(parentYaw) * postVrWorldYaw);
            if (_avatar.Transform is Transform avatarTransform)
                avatarTransform.Rotation = localYaw;

            if (desktopCharacterNode.GetComponent<CharacterPawnComponent>()?.ViewRotationTransform is Transform viewTransform
                && !IsTransformAncestor(viewTransform, _avatar.Transform))
            {
                Quaternion yawDelta = Quaternion.Normalize(postVrWorldYaw * Quaternion.Inverse(_preVrWorldYaw));
                viewTransform.Rotation = Quaternion.Normalize(yawDelta * viewTransform.Rotation);
            }

            return;
        }

        if (_avatar.Transform is Transform standaloneTransform)
        {
            SetTransformWorldTranslation(standaloneTransform, postVrFootWorldPosition);
            Quaternion parentYaw = restoreParent is not null
                ? ExtractPlanarYawRotation(restoreParent.WorldRotation)
                : Quaternion.Identity;
            standaloneTransform.Rotation = Quaternion.Normalize(Quaternion.Inverse(parentYaw) * postVrWorldYaw);
        }
    }

    private static void SetTransformWorldTranslation(Transform transform, Vector3 worldTranslation)
    {
        transform.Translation = transform.Parent is { } parent
            && Matrix4x4.Invert(parent.WorldMatrix, out Matrix4x4 inverseParent)
            ? Vector3.Transform(worldTranslation, inverseParent)
            : worldTranslation;
    }

    private static bool IsTransformAncestor(TransformBase candidateAncestor, TransformBase descendant)
    {
        for (TransformBase? current = descendant.Parent; current is not null; current = current.Parent)
            if (ReferenceEquals(current, candidateAncestor))
                return true;
        return false;
    }

    private static Quaternion ExtractPlanarYawRotation(Quaternion worldRotation)
    {
        if (!float.IsFinite(worldRotation.X) || !float.IsFinite(worldRotation.Y)
            || !float.IsFinite(worldRotation.Z) || !float.IsFinite(worldRotation.W)
            || worldRotation.LengthSquared() < 1.0e-8f)
            return Quaternion.Identity;

        Quaternion normalized = Quaternion.Normalize(worldRotation);
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, normalized);
        forward.Y = 0.0f;
        if (forward.LengthSquared() < 1.0e-8f)
            return Quaternion.Identity;

        float yaw = MathF.Atan2(forward.X, forward.Z);
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
