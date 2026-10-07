using XREngine.Scene.Transforms;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    internal int GpuRestSourceVersion => _particlesVersion;

    internal bool CanCaptureGpuRestInputsInWorldPhase
        => _preUpdateCount != 0 && UseGPU &&
            _effectiveQualityTier != PhysicsChainQualityTier.Sleep;

    /// <summary>Reads one cumulative input-compatibility counter for this chain's world.</summary>
    public long GetGpuRestInputCompatibilityCount(PhysicsChainGpuRestInputCompatibilityReason reason)
        => _cpuBackendWorld?.GetGpuRestInputCompatibilityCount(reason) ?? 0L;

    internal void CollectGpuRestPrerequisiteMatrixTargets(HashSet<TransformBase> targets)
    {
        if (_rootBone is not null)
            targets.Add(_rootBone);
        if (DistantDisable && ReferenceObject is not null)
            targets.Add(ReferenceObject);
    }

    internal bool HasOpaqueGpuRestInputDependency()
    {
        if (HasOpaqueTransformInput(Transform) ||
            HasOpaqueTransformInput(_rootBone) ||
            (DistantDisable && HasOpaqueTransformInput(ReferenceObject)))
            return true;

        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            List<Particle> particles = _particleTrees[treeIndex].Particles;
            for (int particleIndex = 0; particleIndex < particles.Count; ++particleIndex)
            {
                Particle particle = particles[particleIndex];
                if (particle.Transform is not { } transform)
                    continue;
                if (transform.GetType() != typeof(Transform) ||
                    (particle.ParentIndex < 0 && HasOpaqueTransformInput(transform.Parent)))
                    return true;
            }
        }

        if (Colliders is null)
            return false;
        for (int index = 0; index < Colliders.Count; ++index)
        {
            PhysicsChainColliderBase? collider = Colliders[index];
            if (collider is null || !collider.IsActiveInHierarchy)
                continue;
            Transform? shapeTransform = collider switch
            {
                PhysicsChainSphereCollider sphere => sphere.ColliderTransform,
                PhysicsChainCapsuleCollider capsule => capsule.ColliderTransform,
                PhysicsChainBoxCollider box => box.ColliderTransform,
                _ => null,
            };
            if (HasOpaqueTransformInput(collider.Transform) ||
                HasOpaqueTransformInput(collider.RootTransformOverride) ||
                HasOpaqueTransformInput(shapeTransform))
                return true;
        }
        return false;
    }

    private static bool HasOpaqueTransformInput(TransformBase? transform)
    {
        for (TransformBase? current = transform; current is not null; current = current.Parent)
            if (current.GetType() != typeof(Transform))
                return true;
        return false;
    }

    internal void CaptureGpuRestTopology(PhysicsChainGpuRestInputRange range)
    {
        int count = 0;
        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
            count = checked(count + _particleTrees[treeIndex].Particles.Count);
        range.EnsureCapacity(count);
        range.EnsureTreeCapacity(_particleTrees.Count);

        int offset = 0;
        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            range.TreeOffsets[treeIndex] = offset;
            List<Particle> particles = _particleTrees[treeIndex].Particles;
            for (int particleIndex = 0; particleIndex < particles.Count; ++particleIndex)
            {
                Particle particle = particles[particleIndex];
                int index = offset + particleIndex;
                range.Transforms[index] = particle.Transform;
                range.ParentIndices[index] = particle.ParentIndex < 0
                    ? -1
                    : offset + particle.ParentIndex;
                range.InitialTranslations[index] = particle.InitLocalPosition;
                range.InitialRotations[index] = particle.InitLocalRotation;
            }
            offset += particles.Count;
        }

        if (count < range.Count)
            range.Transforms.AsSpan(count, range.Count - count).Clear();
        range.InvalidateLocalMatrices(count);
        range.Count = count;
        range.TreeCount = _particleTrees.Count;
        range.SourceVersion = _particlesVersion;
    }

    internal void CountGpuRestResetNodes(
        Dictionary<TransformBase, int> owners,
        Dictionary<TransformBase, int> firstOwnerSlots,
        int ownerSlot)
    {
        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            List<Particle> particles = _particleTrees[treeIndex].Particles;
            for (int particleIndex = 0; particleIndex < particles.Count; ++particleIndex)
            {
                Transform? transform = particles[particleIndex].Transform;
                if (transform is null)
                    continue;
                owners.TryGetValue(transform, out int count);
                owners[transform] = checked(count + 1);
                firstOwnerSlots.TryAdd(transform, ownerSlot);
            }
        }
    }

    internal void MarkGpuRestInputDependencies(
        Dictionary<TransformBase, int> ownerSlots,
        HashSet<int> compatibilitySlots,
        int readerSlot)
    {
        // These inputs are read before this component restores its own rest pose.
        MarkGpuRestInputDependency(Transform, ownerSlots, compatibilitySlots, readerSlot, false);
        MarkGpuRestInputDependency(_rootBone, ownerSlots, compatibilitySlots, readerSlot, false);
        if (DistantDisable)
            MarkGpuRestInputDependency(ReferenceObject, ownerSlots, compatibilitySlots, readerSlot, false);

        for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
        {
            List<Particle> particles = _particleTrees[treeIndex].Particles;
            for (int particleIndex = 0; particleIndex < particles.Count; ++particleIndex)
            {
                Particle particle = particles[particleIndex];
                if (particle.Transform is not { } transform)
                    continue;
                if (particle.ParentIndex < 0 ||
                    !ReferenceEquals(transform.Parent, particles[particle.ParentIndex].Transform))
                    MarkGpuRestInputDependency(
                        transform.Parent, ownerSlots, compatibilitySlots, readerSlot, true);
            }
        }

        if (Colliders is null)
            return;
        for (int index = 0; index < Colliders.Count; ++index)
        {
            PhysicsChainColliderBase? collider = Colliders[index];
            if (collider is null || !collider.IsActiveInHierarchy)
                continue;
            Transform? shapeTransform = collider switch
            {
                PhysicsChainSphereCollider sphere => sphere.ColliderTransform,
                PhysicsChainCapsuleCollider capsule => capsule.ColliderTransform,
                PhysicsChainBoxCollider box => box.ColliderTransform,
                _ => null,
            };
            MarkGpuRestInputDependency(collider.Transform, ownerSlots, compatibilitySlots, readerSlot, true);
            MarkGpuRestInputDependency(collider.RootTransformOverride, ownerSlots, compatibilitySlots, readerSlot, true);
            MarkGpuRestInputDependency(shapeTransform, ownerSlots, compatibilitySlots, readerSlot, true);
        }
    }

    private static void MarkGpuRestInputDependency(
        TransformBase? transform,
        Dictionary<TransformBase, int> ownerSlots,
        HashSet<int> compatibilitySlots,
        int readerSlot,
        bool includeSelf)
    {
        for (TransformBase? current = transform; current is not null; current = current.Parent)
        {
            if (!ownerSlots.TryGetValue(current, out int ownerSlot) ||
                (!includeSelf && ownerSlot == readerSlot))
                continue;
            // Both ends must publish hierarchy matrices before dependent inputs are read.
            compatibilitySlots.Add(ownerSlot);
            compatibilitySlots.Add(readerSlot);
        }
    }

    internal bool HasGpuRestColliderDependency(Dictionary<TransformBase, int> resetNodeOwners)
    {
        if (Colliders is null)
            return false;

        for (int i = 0; i < Colliders.Count; ++i)
        {
            PhysicsChainColliderBase? collider = Colliders[i];
            if (collider is null || !collider.IsActiveInHierarchy)
                continue;

            if (DependsOnGpuRestResetNode(collider.Transform, resetNodeOwners) ||
                DependsOnGpuRestResetNode(collider.RootTransformOverride, resetNodeOwners))
                return true;
            Transform? shapeTransform = collider switch
            {
                PhysicsChainSphereCollider sphere => sphere.ColliderTransform,
                PhysicsChainCapsuleCollider capsule => capsule.ColliderTransform,
                PhysicsChainBoxCollider box => box.ColliderTransform,
                _ => null,
            };
            if (DependsOnGpuRestResetNode(shapeTransform, resetNodeOwners))
                return true;
        }

        return false;
    }

    private static bool DependsOnGpuRestResetNode(
        TransformBase? transform,
        Dictionary<TransformBase, int> resetNodeOwners)
    {
        for (TransformBase? current = transform; current is not null; current = current.Parent)
            if (resetNodeOwners.ContainsKey(current))
                return true;
        return false;
    }
}
