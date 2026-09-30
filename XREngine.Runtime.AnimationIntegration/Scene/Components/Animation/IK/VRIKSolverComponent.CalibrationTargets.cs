using System.Numerics;
using XREngine.Data.Core;
using XREngine.Input;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

public partial class VRIKSolverComponent
{
    private bool _publishingCalibration;
    private readonly Dictionary<EHumanoidIKTarget, (HumanoidComponent Humanoid, TransformBase Source, Transform Target)> _calibrationTargets = [];

    /// <summary>Raised only after complete target and weight publication. Observer failures do not undo a committed rig.</summary>
    public event Action? CalibrationCommitted;

    internal TransformBase? GetCalibrationSource(EHumanoidIKTarget slot, TransformBase? source)
        => _calibrationTargets.TryGetValue(slot, out var binding) && ReferenceEquals(source, binding.Target)
            ? binding.Source : source;

    internal Matrix4x4 GetFixedCalibrationOffset(EHumanoidIKTarget slot, TransformBase source, Matrix4x4 fallback)
    {
        Matrix4x4 supplied = Humanoid.GetIKTarget(slot).offset;
        if (supplied != Matrix4x4.Identity)
            return supplied;
        return _calibrationTargets.TryGetValue(slot, out var binding) && ReferenceEquals(binding.Source, source) && IsLive(binding.Target)
            ? binding.Target.LocalMatrix : fallback;
    }

    public TransformBase? GetCalibratedTarget(EHumanoidIKTarget slot) => ResolveCalibrationTarget(slot);

    public Transform? ResolveCalibrationTarget(EHumanoidIKTarget slot)
    {
        HumanoidComponent? humanoid = TryGetHumanoid();
        TransformBase? source = humanoid?.GetIKTargetTransform(slot);
        if (!IsLive(source))
            return null;
        if (_calibrationTargets.TryGetValue(slot, out var binding) && ReferenceEquals(binding.Humanoid, humanoid)
            && IsLive(binding.Target) && (ReferenceEquals(source, binding.Source) || ReferenceEquals(source, binding.Target)))
        {
            if (!_publishingCalibration && binding.Source is IVrTrackingPoseSource tracked
                && (!string.Equals(_boundIdentity[(int)slot], tracked.TrackingIdentity, StringComparison.Ordinal)
                    || _boundGeneration[(int)slot] != tracked.TrackingSessionGeneration))
                return null;
            return binding.Target;
        }
        return source as Transform;
    }

    private static bool IsLive(TransformBase? transform)
        => transform is { IsDestroyed: false, IsDestroyQueued: false }
            && transform.SceneNode is { IsDestroyed: false, IsDestroyQueued: false };

    internal void ReleaseCalibrationTarget(EHumanoidIKTarget slot)
    {
        if (!_calibrationTargets.Remove(slot, out var binding))
            return;
        if (ReferenceEquals(binding.Humanoid.GetIKTargetTransform(slot), binding.Target))
            binding.Humanoid.ClearIKTarget(slot);
        binding.Target.SceneNode?.Destroy(true);
        _trackingWeight[(int)slot] = 0;
        _slotStates[(int)slot] = default;
    }

    private void ReleaseCalibrationTargets()
    {
        foreach (var pair in _calibrationTargets)
        {
            var binding = pair.Value;
            if (ReferenceEquals(binding.Humanoid.GetIKTargetTransform(pair.Key), binding.Target))
                binding.Humanoid.ClearIKTarget(pair.Key);
            binding.Target.SceneNode?.Destroy(true);
        }
        _calibrationTargets.Clear();
        Array.Clear(_trackingWeight);
        Array.Clear(_slotStates);
        Solver.Spine.HeadTarget = null;
        Solver.Spine.HipsTarget = null;
        Solver.LeftArm.Target = null;
        Solver.RightArm.Target = null;
        Solver.LeftLeg.Target = null;
        Solver.RightLeg.Target = null;
        Solver.Spine.ChestTarget = null;
        Solver.LeftArm.UpperArmTarget = null;
        Solver.RightArm.UpperArmTarget = null;
        Solver.LeftLeg.KneeTarget = null;
        Solver.RightLeg.KneeTarget = null;
    }

    /// <summary>Stages new children, publishes one transaction, then releases superseded nodes.</summary>
    internal void CommitCalibrationTargets(ReadOnlySpan<VrCalibrationTarget> proposed, Action applyWeights)
    {
        var previous = new Dictionary<EHumanoidIKTarget, (HumanoidComponent Humanoid, TransformBase Source, Transform Target)>(_calibrationTargets);
        var staged = new Dictionary<EHumanoidIKTarget, (HumanoidComponent Humanoid, TransformBase Source, Transform Target)>(11);
        var created = new List<Transform>(11);
        var tuples = new (TransformBase? tfm, Matrix4x4 offset)[11];
        var locals = new Matrix4x4[11];
        float[] weights = CaptureCalibrationWeights();
        bool plantFeet = Solver.PlantFeet;
        Transform?[] solverTargets = [Solver.Spine.HeadTarget, Solver.Spine.HipsTarget, Solver.LeftArm.Target, Solver.RightArm.Target,
            Solver.LeftLeg.Target, Solver.RightLeg.Target, Solver.LeftArm.UpperArmTarget, Solver.RightArm.UpperArmTarget,
            Solver.LeftLeg.KneeTarget, Solver.RightLeg.KneeTarget, Solver.Spine.ChestTarget];
        for (int i = 0; i < 11; i++)
        {
            tuples[i] = Humanoid.GetIKTarget((EHumanoidIKTarget)i);
            if (previous.TryGetValue((EHumanoidIKTarget)i, out var binding))
                locals[i] = binding.Target.LocalMatrix;
        }
        _publishingCalibration = true;
        try
        {
            foreach (VrCalibrationTarget proposal in proposed)
            {
                Transform target;
                if (previous.TryGetValue(proposal.Slot, out var old) && ReferenceEquals(old.Humanoid, Humanoid)
                    && ReferenceEquals(old.Source, proposal.Source) && IsLive(old.Target))
                    target = old.Target;
                else
                {
                    target = new Transform();
                    _ = new SceneNode(proposal.Slot + " IK Target", target);
                    created.Add(target);
                    // New nodes have no consumers. Never reparent or destroy an old binding during staging.
                    target.SetParent(proposal.Source, false, EParentAssignmentMode.Immediate);
                }
                staged.Add(proposal.Slot, (Humanoid, proposal.Source, target));
            }
            // Publish no intermediate target/weight property notifications. Matrix caches are explicitly refreshed;
            // source ownership hierarchy notifications happened during staging, outside this scope.
            using (XRBase.SuppressPropertyNotifications())
            {
                foreach (VrCalibrationTarget proposal in proposed)
                {
                    Transform target = staged[proposal.Slot].Target;
                    target.DeriveLocalMatrix(proposal.Offset);
                    target.RecalcLocal();
                    target.RecalculateMatrices(true);
                }
                _calibrationTargets.Clear();
                foreach (var pair in staged)
                    _calibrationTargets.Add(pair.Key, pair.Value);
                for (int i = 0; i < 11; i++)
                    Humanoid.SetIKTarget((EHumanoidIKTarget)i, staged.TryGetValue((EHumanoidIKTarget)i, out var binding) ? binding.Source : null, Matrix4x4.Identity);
                SyncSolverTargets();
                applyWeights();
            }
        }
        catch
        {
            // Compensating writes bypass arbitrary property subscribers. Matrix callbacks can still throw,
            // so restore each independent cache before continuing compensation.
            using (XRBase.SuppressPropertyNotifications())
            {
                _calibrationTargets.Clear();
                foreach (var pair in previous)
                {
                    _calibrationTargets.Add(pair.Key, pair.Value);
                    if (!IsLive(pair.Value.Target))
                        continue;
                    pair.Value.Target.DeriveLocalMatrix(locals[(int)pair.Key]);
                    try { pair.Value.Target.RecalcLocal(); } catch { }
                    try { pair.Value.Target.RecalculateMatrices(true); } catch { }
                }
                for (int i = 0; i < 11; i++)
                    Humanoid.SetIKTarget((EHumanoidIKTarget)i, tuples[i].tfm, tuples[i].offset);
                Solver.Spine.HeadTarget = solverTargets[0]; Solver.Spine.HipsTarget = solverTargets[1];
                Solver.LeftArm.Target = solverTargets[2]; Solver.RightArm.Target = solverTargets[3];
                Solver.LeftLeg.Target = solverTargets[4]; Solver.RightLeg.Target = solverTargets[5];
                Solver.LeftArm.UpperArmTarget = solverTargets[6]; Solver.RightArm.UpperArmTarget = solverTargets[7];
                Solver.LeftLeg.KneeTarget = solverTargets[8]; Solver.RightLeg.KneeTarget = solverTargets[9];
                Solver.Spine.ChestTarget = solverTargets[10];
                RestoreCalibrationWeights(weights);
                Solver.PlantFeet = plantFeet;
            }
            foreach (Transform target in created)
                DestroyRetiredTarget(target);
            throw;
        }
        finally { _publishingCalibration = false; }
        ResetTrackingWeights();
        // Commit point: the complete rig and weights are visible. Cleanup/observer failures are diagnostics,
        // never a reported transaction failure after irreversible teardown.
        foreach (var pair in previous)
            if (!staged.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current.Target, pair.Value.Target))
                DestroyRetiredTarget(pair.Value.Target);
        if (CalibrationCommitted is { } committed)
            foreach (Action observer in committed.GetInvocationList())
                try { observer(); }
                catch (Exception exception) { Debug.Animation("Calibration observer failed: " + exception.Message); }
    }

    private static void DestroyRetiredTarget(Transform target)
    {
        try { target.SceneNode?.Destroy(true); }
        catch (Exception exception) { Debug.Animation("Retired calibration target cleanup failed: " + exception.Message); }
    }

    private float[] CaptureCalibrationWeights()
        => [Solver.Spine.HipsPositionWeight, Solver.Spine.HipsRotationWeight, Solver.Spine.ChestTargetWeight,
            Solver.LeftArm.Settings.PositionWeight, Solver.LeftArm.Settings.RotationWeight, Solver.LeftArm.Settings.UpperArmTargetWeight,
            Solver.RightArm.Settings.PositionWeight, Solver.RightArm.Settings.RotationWeight, Solver.RightArm.Settings.UpperArmTargetWeight,
            Solver.LeftLeg.PositionWeight, Solver.LeftLeg.RotationWeight, Solver.LeftLeg.KneeTargetWeight,
            Solver.RightLeg.PositionWeight, Solver.RightLeg.RotationWeight, Solver.RightLeg.KneeTargetWeight, Solver.Spine.PositionWeight, Solver.Spine.RotationWeight];

    private void RestoreCalibrationWeights(ReadOnlySpan<float> values)
    {
        Solver.Spine.HipsPositionWeight = values[0]; Solver.Spine.HipsRotationWeight = values[1]; Solver.Spine.ChestTargetWeight = values[2];
        Solver.LeftArm.Settings.PositionWeight = values[3]; Solver.LeftArm.Settings.RotationWeight = values[4]; Solver.LeftArm.Settings.UpperArmTargetWeight = values[5];
        Solver.RightArm.Settings.PositionWeight = values[6]; Solver.RightArm.Settings.RotationWeight = values[7]; Solver.RightArm.Settings.UpperArmTargetWeight = values[8];
        Solver.LeftLeg.PositionWeight = values[9]; Solver.LeftLeg.RotationWeight = values[10]; Solver.LeftLeg.KneeTargetWeight = values[11];
        Solver.RightLeg.PositionWeight = values[12]; Solver.RightLeg.RotationWeight = values[13]; Solver.RightLeg.KneeTargetWeight = values[14];
        Solver.Spine.PositionWeight = values[15]; Solver.Spine.RotationWeight = values[16];
    }
}
