using System.Numerics;
using XREngine.Components;

namespace XREngine.Rendering.Compute;

/// <summary>Owns one complete update-to-render chain input generation.</summary>
internal sealed class PhysicsChainRenderInputPacket
{
    internal GPUPhysicsChainDispatcher.GPUParticleData[] Particles = [];
    internal GPUPhysicsChainDispatcher.GPUParticleStaticData[] ParticleStaticData = [];
    internal GPUPhysicsChainDispatcher.GPUParticleTreeData[] Trees = [];
    internal Matrix4x4[] Transforms = [];
    internal GPUPhysicsChainDispatcher.GPUColliderData[] Colliders = [];
    internal PhysicsChainGpuBone[] Bones = [];
    internal PhysicsChainGpuSpatialInput SpatialInput;
    internal bool AffineInputValid;
    internal int FirstInvalidAffineInputIndex = -1;
    private int _validatedTransformSignature = int.MinValue;
    private int _validatedTransformCount = -1;
    private int _validatedParticleCount = -1;

    internal ulong Sequence;
    internal bool HasData;
    internal float DeltaTime;
    internal float ObjectScale;
    internal float Weight;
    internal Vector3 Force;
    internal Vector3 Gravity;
    internal Vector3 ObjectMove;
    internal int FreezeAxis;
    internal int LoopCount;
    internal float TimeVar;
    internal int UpdateMode;
    internal int DispatchIsolationKey;
    internal int ExecutionGeneration;
    internal long ReadbackSourceGeneration;
    internal long SubmissionId;
    internal int StaticDataVersion;
    internal int TreeDataVersion;
    internal int ParticleStateVersion;
    internal int TransformDataSignature;
    internal int ColliderDataSignature;
    internal int BoneStructureSignature;
    internal bool EffectiveGpuDrivenSkinning;
    internal uint Enabled = 1u;
    internal uint Relevant = 1u;
    internal uint SleepState;
    internal uint QualityTier;
    internal uint Cadence = 1u;
    internal uint Phase;
    internal uint FeatureMask;
    internal bool SchedulingMetadataInitialized;

    internal void CopySchedulingFrom(PhysicsChainRenderInputPacket source)
    {
        Enabled = source.Enabled;
        Relevant = source.Relevant;
        SleepState = source.SleepState;
        QualityTier = source.QualityTier;
        Cadence = source.Cadence;
        Phase = source.Phase;
        FeatureMask = source.FeatureMask;
        SchedulingMetadataInitialized = source.SchedulingMetadataInitialized;
    }

    internal void CopyDataFrom(PhysicsChainRenderInputPacket source)
    {
        Copy(source.Particles, ref Particles);
        Copy(source.ParticleStaticData, ref ParticleStaticData);
        Copy(source.Trees, ref Trees);
        Copy(source.Transforms, ref Transforms);
        Copy(source.Colliders, ref Colliders);
        Copy(source.Bones, ref Bones);
        AffineInputValid = source.AffineInputValid;
        FirstInvalidAffineInputIndex = source.FirstInvalidAffineInputIndex;
        _validatedTransformSignature = source._validatedTransformSignature;
        _validatedTransformCount = source._validatedTransformCount;
        _validatedParticleCount = source._validatedParticleCount;
        SpatialInput = source.SpatialInput;
        DeltaTime = source.DeltaTime;
        ObjectScale = source.ObjectScale;
        Weight = source.Weight;
        Force = source.Force;
        Gravity = source.Gravity;
        ObjectMove = source.ObjectMove;
        FreezeAxis = source.FreezeAxis;
        LoopCount = source.LoopCount;
        TimeVar = source.TimeVar;
        UpdateMode = source.UpdateMode;
        DispatchIsolationKey = source.DispatchIsolationKey;
        ExecutionGeneration = source.ExecutionGeneration;
        ReadbackSourceGeneration = source.ReadbackSourceGeneration;
        SubmissionId = source.SubmissionId;
        StaticDataVersion = source.StaticDataVersion;
        TreeDataVersion = source.TreeDataVersion;
        ParticleStateVersion = source.ParticleStateVersion;
        TransformDataSignature = source.TransformDataSignature;
        ColliderDataSignature = source.ColliderDataSignature;
        BoneStructureSignature = source.BoneStructureSignature;
        EffectiveGpuDrivenSkinning = source.EffectiveGpuDrivenSkinning;
        HasData = source.HasData;
    }

    internal void ValidateAffineInputs(int transformSignature)
    {
        if (HasData && _validatedTransformSignature == transformSignature &&
            _validatedTransformCount == Transforms.Length &&
            _validatedParticleCount == Particles.Length)
            return;

        _validatedTransformSignature = transformSignature;
        _validatedTransformCount = Transforms.Length;
        _validatedParticleCount = Particles.Length;
        AffineInputValid = false;
        FirstInvalidAffineInputIndex = -1;
        if (Transforms.Length != Particles.Length)
        {
            FirstInvalidAffineInputIndex = Math.Min(Transforms.Length, Particles.Length);
            return;
        }
        for (int index = 0; index < Transforms.Length; ++index)
        {
            if (PhysicsChainAffineInput.IsFiniteAffine(in Transforms[index]))
                continue;
            FirstInvalidAffineInputIndex = index;
            return;
        }
        AffineInputValid = true;
    }

    private static void Copy<T>(T[] source, ref T[] destination)
    {
        if (destination.Length != source.Length)
            destination = source.Length == 0 ? [] : new T[source.Length];
        source.AsSpan().CopyTo(destination);
    }
}
