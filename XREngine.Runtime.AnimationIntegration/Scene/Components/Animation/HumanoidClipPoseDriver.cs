using System.Numerics;
using XREngine.Animation;

namespace XREngine.Components.Animation;

/// <summary>
/// Runs one external humanoid frame for an evaluator that writes its own pose values, in the order of the
/// animation components: begin the frame, add the Body and root motion of the clips, write the muscle and
/// IK values, solve the humanoid, then solve IK once. The Body and root results come from the leaf path
/// of the state machine, so the values do not depend on a playback clock. The caller places the root from
/// <see cref="TrySolvePose"/> before <see cref="SolveIK"/>.
/// </summary>
public sealed class HumanoidClipPoseDriver
{
    private const ulong LifecycleGeneration = 1UL;

    private readonly HumanoidComponent _humanoid;
    private readonly HumanoidStateMachineRootMotionLeafState[] _leaves;
    private readonly HumanoidStateMachineRootMotionFrame _frame;
    private HumanoidIKSolverComponent? _ikSolver;
    private bool _ownsTransaction;

    /// <summary>Creates a driver.</summary>
    /// <param name="humanoid">The humanoid to drive.</param>
    /// <param name="clipCapacity">The number of different clips that the caller can add.</param>
    public HumanoidClipPoseDriver(HumanoidComponent humanoid, int clipCapacity)
    {
        ArgumentNullException.ThrowIfNull(humanoid);
        _humanoid = humanoid;
        _leaves = new HumanoidStateMachineRootMotionLeafState[clipCapacity];
        for (int i = 0; i < _leaves.Length; i++)
            _leaves[i] = new HumanoidStateMachineRootMotionLeafState();
        _frame = new HumanoidStateMachineRootMotionFrame(clipCapacity);
    }

    /// <summary>
    /// Starts a frame, and makes the humanoid discard the RootT and RootQ values of the store write.
    /// </summary>
    /// <returns>False if another evaluator owns the Body and root transaction. Nothing changes in that case.</returns>
    public bool TryBeginFrame()
    {
        if (!_humanoid.BeginImportedBodySampleTransaction(this, HumanoidImportedBodySample.Neutral, hasCanonicalSample: false))
            return false;

        _ownsTransaction = true;
        _frame.Clear();
        _ikSolver = _humanoid.SceneNode.GetComponent<VRIKSolverComponent>() is null
            ? _humanoid.SceneNode.GetComponent<HumanoidIKSolverComponent>()
            : null;
        _ikSolver?.BeginAnimationDrivenGoalFrame();
        return true;
    }

    /// <summary>Adds the Body and root motion of a clip to the frame. A clip with no root motion adds nothing.</summary>
    /// <param name="index">The stable index of this clip, below the capacity.</param>
    /// <param name="clip">The clip.</param>
    /// <param name="second">The clip time in seconds.</param>
    /// <param name="weight">The blend weight of the clip.</param>
    /// <returns>False if the humanoid cannot evaluate the clip. Call <see cref="CancelFrame"/> in that case.</returns>
    public bool TryAddClip(int index, AnimationClip clip, float second, float weight)
    {
        ArgumentNullException.ThrowIfNull(clip);
        double normalizedTime = clip.LengthInSeconds > 0f ? second / (double)clip.LengthInSeconds : 0.0;
        ulong occurrenceId = (ulong)index + 1UL;
        if (!clip.TryCreateHumanoidMotionContribution(normalizedTime, weight, occurrenceId, LifecycleGeneration, inheritedMirror: false, out HumanoidMotionContribution contribution))
            return true;

        return _leaves[index].TryPrepare(in contribution, _humanoid) && _frame.TryAdd(_leaves[index]);
    }

    /// <summary>
    /// Gives the added clips to the humanoid and forgets the muscle values of the last frame, so that a
    /// value with no clip now has no effect. Write the store values after this call.
    /// </summary>
    public void StageMotion()
    {
        _humanoid.ClearRuntimeMuscleState();
        _humanoid.StageStateMachineRootMotionFrame(this, _frame);
    }

    /// <summary>Ends a frame that failed before the solve. The last committed pose stays.</summary>
    public void CancelFrame()
    {
        EndTransaction();
        _ikSolver?.RejectAnimationDrivenGoalFrame();
        _humanoid.ClearStateMachineRootMotionFrame(this);
    }

    /// <summary>Solves the humanoid once from the written values. A later scene tick does not solve this frame again.</summary>
    /// <param name="rootPosition">The projected root position. It is relative to the clip start and has only valid channels.</param>
    /// <param name="rootRotation">The projected root rotation, or identity if the clips give no rotation.</param>
    /// <returns>False if the humanoid rejected the frame. The last committed pose stays in that case.</returns>
    public bool TrySolvePose(out Vector3 rootPosition, out Quaternion rootRotation)
    {
        rootPosition = Vector3.Zero;
        rootRotation = Quaternion.Identity;

        _ikSolver?.CompleteAnimationDrivenGoalFrame();
        EndTransaction();
        _humanoid.ApplyCurrentStateMachineMusclePoseImmediately();
        if (!_humanoid.WasLastNativeFrameAccepted)
            return false;

        if (_humanoid.TryGetAcceptedRootMotionInput(this, out HumanoidProjectedRootPose pose, out _))
            ReadValidChannels(pose, out rootPosition, out rootRotation);
        return true;
    }

    /// <summary>Solves IK once from the solved pose and the placed root. The scheduled solve of this frame then does not run.</summary>
    public void SolveIK()
        => _ikSolver?.UpdateSolverExternal();

    /// <summary>Forgets the motion of the driver. The bones and the muscle values stay.</summary>
    public void ReleaseMotion()
        => _humanoid.ClearStateMachineRootMotionFrame(this);

    /// <summary>
    /// Forgets the motion, the muscle values, the Body sample, and the IK goals, so that a later scene tick
    /// does not solve over the bones. The caller writes the bones.
    /// </summary>
    public void ClearPoseInputs()
    {
        _humanoid.ClearStateMachineRootMotionFrame(this);
        _humanoid.ResetRootMotionBaseline();
        _humanoid.ClearRuntimeMuscleState();
        _humanoid.SceneNode.GetComponent<HumanoidIKSolverComponent>()?.ClearAnimatedIKGoals();
    }

    private void EndTransaction()
    {
        if (!_ownsTransaction)
            return;
        _ownsTransaction = false;
        _humanoid.CancelImportedBodySampleTransaction(this);
    }

    private static void ReadValidChannels(HumanoidProjectedRootPose pose, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.Zero;
        if ((pose.Channels & EHumanoidProjectedRootChannels.PositionXZ) != 0)
        {
            position.X = float.IsFinite(pose.Position.X) ? pose.Position.X : 0f;
            position.Z = float.IsFinite(pose.Position.Z) ? pose.Position.Z : 0f;
        }
        if ((pose.Channels & EHumanoidProjectedRootChannels.PositionY) != 0)
            position.Y = float.IsFinite(pose.Position.Y) ? pose.Position.Y : 0f;

        float length = pose.Rotation.LengthSquared();
        rotation = (pose.Channels & EHumanoidProjectedRootChannels.RotationYaw) != 0 && float.IsFinite(length) && length > 1e-8f
            ? Quaternion.Normalize(pose.Rotation)
            : Quaternion.Identity;
    }
}
