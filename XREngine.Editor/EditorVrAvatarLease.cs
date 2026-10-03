using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using XREngine.Components;
using XREngine.Components.Animation;
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
    private readonly SceneNode _character;
    private readonly TransformBase? _originalParent;
    private readonly Matrix4x4 _originalLocalMatrix;
    private readonly RuntimeWorld _world;
    private readonly bool _wasRuntimeRoot;
    private readonly List<XRComponent> _pausedWriters = [];
    private readonly List<bool> _writerActive = [];
    private readonly List<bool> _writerSuspended = [];
    private readonly List<bool> _resumeClips = [];
    private int _pausedWriterCount;
    private VRHeightScaleComponent? _vrScale;
    private VRIKSolverComponent? _vrSolver;
    private VRPlayerCharacterComponent? _player;
    private bool _disposed;

    private EditorVrAvatarLease(SceneNode avatar, SceneNode character, RuntimeWorld world)
    {
        _avatar = avatar;
        _character = character;
        _originalParent = avatar.Transform.Parent;
        _originalLocalMatrix = avatar.Transform.LocalMatrix;
        _world = world;
        _wasRuntimeRoot = _originalParent is null && world.RootNodes.Any(root => ReferenceEquals(root, avatar));
        avatar.IterateComponents<XRComponent>(component =>
        {
            if (component is not (HeightScaleComponent or BaseIKSolverComponent or
                AnimStateMachineComponent or AnimationClipComponent or PhysicsChainComponent))
                return;
            _pausedWriters.Add(component);
            _writerActive.Add(component.IsActive);
            _writerSuspended.Add(component is AnimStateMachineComponent machine && machine.SuspendedByClip);
            _resumeClips.Add(component is AnimationClipComponent clip && clip.IsPlaying && !clip.IsPaused);
        }, true);
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

        var candidate = new EditorVrAvatarLease(avatar, character, world);
        try
        {
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

        if (_player is not null)
            _player.IsActive = false;
        if (_character.GetComponent<VrLocalAvatarVisibilityComponent>() is { } visibility)
            visibility.IsActive = false;

        if (_vrSolver is not null && ReferenceEquals(_avatar.GetComponent<VRIKSolverComponent>(), _vrSolver))
            _avatar.RemoveComponent<VRIKSolverComponent>();
        if (_vrScale is not null && ReferenceEquals(_avatar.GetComponent<VRHeightScaleComponent>(), _vrScale))
            _avatar.RemoveComponent<VRHeightScaleComponent>();

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
    }
}
