using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

public partial class VRIKSolverComponent
{
    private readonly Dictionary<EHumanoidIKTarget, (HumanoidComponent Humanoid, TransformBase Source, Transform Target)> _calibrationTargets = [];

    internal TransformBase? GetCalibrationSource(EHumanoidIKTarget slot, TransformBase? source)
        => _calibrationTargets.TryGetValue(slot, out var binding) && ReferenceEquals(source, binding.Target)
            ? binding.Source
            : source;

    /// <summary>Retains calibrated children separately from the humanoid's raw tracking sources.</summary>
    internal Transform GetOrCreateCalibrationTarget(EHumanoidIKTarget slot, TransformBase source, string name)
    {
        HumanoidComponent humanoid = Humanoid;
        Transform target;
        if (_calibrationTargets.TryGetValue(slot, out var binding))
        {
            // A caller may pass the already-calibrated target back into calibration.
            if (ReferenceEquals(source, binding.Target))
                source = binding.Source;

            if (ReferenceEquals(binding.Humanoid, humanoid) && !binding.Target.IsDestroyed && !binding.Target.IsDestroyQueued &&
                binding.Target.SceneNode is { IsDestroyed: false, IsDestroyQueued: false })
                target = binding.Target;
            else
            {
                ReleaseCalibrationTarget(slot);
                source.SceneNode!.NewChildWithTransform(out target, name);
            }
        }
        else
            source.SceneNode!.NewChildWithTransform(out target, name);

        if (!ReferenceEquals(target.Parent, source))
            target.SetParent(source, false, EParentAssignmentMode.Immediate);

        // Preserve a source's existing tuple offset; calibration still owns its child offset.
        var current = humanoid.GetIKTarget(slot);
        if (!ReferenceEquals(current.tfm, source))
            humanoid.SetIKTarget(slot, source, ReferenceEquals(current.tfm, target) ? current.offset : Matrix4x4.Identity);

        _calibrationTargets[slot] = (humanoid, source, target);
        return target;
    }

    private Transform? ResolveCalibrationTarget(EHumanoidIKTarget slot)
    {
        HumanoidComponent? humanoid = TryGetHumanoid();
        TransformBase? source = humanoid?.GetIKTargetTransform(slot);
        if (source is null || source.IsDestroyed || source.IsDestroyQueued ||
            source.SceneNode is { IsDestroyed: true } or { IsDestroyQueued: true })
            return null;
        if (_calibrationTargets.TryGetValue(slot, out var binding) &&
            ReferenceEquals(binding.Humanoid, humanoid) && !binding.Target.IsDestroyed && !binding.Target.IsDestroyQueued &&
            binding.Target.SceneNode is { IsDestroyed: false, IsDestroyQueued: false } &&
            (ReferenceEquals(source, binding.Source) || ReferenceEquals(source, binding.Target)))
            return binding.Target;

        return source as Transform;
    }

    /// <summary>Releases only nodes created by this solver, never tracking sources or externally supplied targets.</summary>
    internal void ReleaseCalibrationTarget(EHumanoidIKTarget slot)
    {
        if (!_calibrationTargets.Remove(slot, out var binding))
            return;

        if (ReferenceEquals(binding.Humanoid.GetIKTargetTransform(slot), binding.Target))
            binding.Humanoid.ClearIKTarget(slot);
        binding.Target.SceneNode?.Destroy(true);
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
        Solver.Spine.HeadTarget = null;
        Solver.Spine.HipsTarget = null;
        Solver.LeftArm.Target = null;
        Solver.RightArm.Target = null;
        Solver.LeftLeg.Target = null;
        Solver.RightLeg.Target = null;
    }
}
