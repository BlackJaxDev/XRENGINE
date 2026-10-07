using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using XREngine.Animation;
using XREngine.Data.Transforms;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>Owns one chain's reused rest-pose input range in its runtime world.</summary>
internal sealed class PhysicsChainGpuRestInputRange
{
    internal Transform?[] Transforms = [];
    internal int[] ParentIndices = [];
    internal Vector3[] InitialTranslations = [];
    internal Quaternion[] InitialRotations = [];
    private AffineMatrix4x3[] _localMatrices = [];
    private Vector3[] _localTranslations = [];
    private Vector3[] _localScales = [];
    private ETransformOrder[] _localOrders = [];
    private bool[] _localMatrixValid = [];
    internal Matrix4x4[] Matrices = [];
    internal int[] TreeOffsets = [];
    internal Vector3[] TreeAnchorMinimum = [];
    internal Vector3[] TreeAnchorMaximum = [];
    internal int Count;
    internal int TreeCount;
    internal int SourceVersion = -1;
    internal ulong MatrixGeneration;
    internal float MaximumStretch;
    internal bool SpatialValid;
    internal ulong CapturedPhase;
    internal ulong ConsumedPhase;
    internal PhysicsChainRuntimeHandle CapturedHandle;
    internal ExceptionDispatchInfo? CaptureFault;
    internal readonly PhysicsChainRigidGpuRestInputCache RigidCache = new();
    internal long CachedCaptureGeneration;
    internal long CachedOwnershipGeneration;

    internal void EnsureCapacity(int count)
    {
        if (Transforms.Length >= count)
            return;

        int capacity = Math.Max(count, Math.Max(Transforms.Length * 2, 8));
        Array.Resize(ref Transforms, capacity);
        Array.Resize(ref ParentIndices, capacity);
        Array.Resize(ref InitialTranslations, capacity);
        Array.Resize(ref InitialRotations, capacity);
        Array.Resize(ref _localMatrices, capacity);
        Array.Resize(ref _localTranslations, capacity);
        Array.Resize(ref _localScales, capacity);
        Array.Resize(ref _localOrders, capacity);
        Array.Resize(ref _localMatrixValid, capacity);
        Array.Resize(ref Matrices, capacity);
    }

    /// <summary>Invalidates rest matrices after their topology or rest pose changes.</summary>
    internal void InvalidateLocalMatrices(int count)
        => _localMatrixValid.AsSpan(0, count).Clear();

    internal void EnsureTreeCapacity(int count)
    {
        if (TreeOffsets.Length >= count)
            return;

        int capacity = Math.Max(count, Math.Max(TreeOffsets.Length * 2, 4));
        Array.Resize(ref TreeOffsets, capacity);
        Array.Resize(ref TreeAnchorMinimum, capacity);
        Array.Resize(ref TreeAnchorMaximum, capacity);
    }

    internal PhysicsChainGpuRestInputCompatibilityReason Capture(
        Dictionary<TransformBase, int> resetNodeOwners,
        HashSet<TransformBase> prerequisiteMatrixTargets)
    {
        int treeIndex = -1;
        MaximumStretch = 0.0f;
        SpatialValid = true;
        bool generationAdvanced = false;
        for (int index = 0; index < TreeCount; ++index)
        {
            TreeAnchorMinimum[index] = new(float.PositiveInfinity);
            TreeAnchorMaximum[index] = new(float.NegativeInfinity);
        }
        for (int i = 0; i < Count; ++i)
        {
            while (treeIndex + 1 < TreeCount && i >= TreeOffsets[treeIndex + 1])
                ++treeIndex;
            Transform? transform = Transforms[i];
            int parentIndex = ParentIndices[i];
            Matrix4x4 matrix;
            if (transform is null)
            {
                if ((uint)parentIndex >= (uint)i)
                    return PhysicsChainGpuRestInputCompatibilityReason.ChangedParent;
                matrix = Matrices[parentIndex];
            }
            else
            {
                if (transform.GetType() != typeof(Transform))
                    return PhysicsChainGpuRestInputCompatibilityReason.CustomTransform;
                if (transform.ForceManualRecalc)
                    return PhysicsChainGpuRestInputCompatibilityReason.ManualHierarchyRefresh;
                if (resetNodeOwners.TryGetValue(transform, out int ownerCount) && ownerCount > 1)
                    return PhysicsChainGpuRestInputCompatibilityReason.SharedResetNode;

                TransformState state = transform.FrameState;
                // A reset must keep its hierarchy effects when an authored rest value changed.
                if (!HasSameBits(in state.Rotation, in InitialRotations[i]) ||
                    (parentIndex >= 0 && !HasSameBits(in state.Translation, in InitialTranslations[i])))
                    return PhysicsChainGpuRestInputCompatibilityReason.AuthoredRestResetRequired;

                Matrix4x4 parentWorld;
                if (parentIndex >= 0)
                {
                    if ((uint)parentIndex >= (uint)i ||
                        !ReferenceEquals(transform.Parent, Transforms[parentIndex]))
                        return PhysicsChainGpuRestInputCompatibilityReason.ChangedParent;
                    parentWorld = Matrices[parentIndex];
                }
                else
                {
                    for (TransformBase? ancestor = transform.Parent; ancestor is not null; ancestor = ancestor.Parent)
                    {
                        if (resetNodeOwners.ContainsKey(ancestor))
                            return PhysicsChainGpuRestInputCompatibilityReason.ExternalParentDependency;
                        // Serial preparation can refresh this parent after the world capture.
                        if (prerequisiteMatrixTargets.Contains(ancestor))
                            return PhysicsChainGpuRestInputCompatibilityReason.PrerequisiteMatrixRefresh;
                    }
                    parentWorld = transform.Parent?.WorldMatrix ?? Matrix4x4.Identity;
                }

                Vector3 translation = parentIndex < 0 ? state.Translation : InitialTranslations[i];
                AffineMatrix4x3 local = GetLocalRestMatrix(i, translation, state.Scale, state.Order);
                matrix = local.ToMatrix4x4() * parentWorld;
            }

            if (Matrices[i] != matrix)
            {
                if (!generationAdvanced)
                {
                    MatrixGeneration = checked(MatrixGeneration + 1);
                    generationAdvanced = true;
                }
                Matrices[i] = matrix;
            }
            Vector3 worldPosition = matrix.Translation;
            SpatialValid &= float.IsFinite(worldPosition.X) && float.IsFinite(worldPosition.Y) &&
                float.IsFinite(worldPosition.Z) && matrix.M14 == 0.0f && matrix.M24 == 0.0f &&
                matrix.M34 == 0.0f && matrix.M44 == 1.0f;
            float stretch = PhysicsChainGpuSpatialInput.MaximumLinearStretch(in matrix);
            SpatialValid &= float.IsFinite(stretch);
            MaximumStretch = MathF.Max(MaximumStretch, stretch);
            if (parentIndex < 0 || parentIndex >= i)
            {
                TreeAnchorMinimum[treeIndex] = Vector3.Min(TreeAnchorMinimum[treeIndex], worldPosition);
                TreeAnchorMaximum[treeIndex] = Vector3.Max(TreeAnchorMaximum[treeIndex], worldPosition);
            }
        }

        return PhysicsChainGpuRestInputCompatibilityReason.None;
    }

    private AffineMatrix4x3 GetLocalRestMatrix(
        int index,
        Vector3 translation,
        Vector3 scale,
        ETransformOrder order)
    {
        // The parent matrix is read on every capture. Only the local rest matrix is retained.
        if (!_localMatrixValid[index] || _localOrders[index] != order ||
            !HasSameBits(in _localTranslations[index], in translation) ||
            !HasSameBits(in _localScales[index], in scale))
        {
            _localMatrices[index] = PhysicsChainRestTransformMatrix.Compose(
                translation, InitialRotations[index], scale, order);
            _localTranslations[index] = translation;
            _localScales[index] = scale;
            _localOrders[index] = order;
            _localMatrixValid[index] = true;
        }
        return _localMatrices[index];
    }

    internal static bool HasSameBits<T>(in T first, in T second) where T : unmanaged
        => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in first), 1))
            .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in second), 1)));
}
