using System.Numerics;
using System.Diagnostics;
using XREngine.Animation;
using XREngine.Data.Core;
using XREngine.Data.Transforms;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>Retains one tree's child rest matrices until an authored child input changes.</summary>
internal sealed class PhysicsChainRigidGpuRestInputCache
{
    private static readonly string[] ChildInputProperties =
    [
        nameof(Transform.Translation), nameof(Transform.Rotation), nameof(Transform.Scale),
        nameof(Transform.Order), nameof(Transform.FrameState), nameof(TransformBase.Parent),
        nameof(TransformBase.World), nameof(TransformBase.SceneNode),
        nameof(TransformBase.Children), nameof(TransformBase.ForceManualRecalc),
        nameof(TransformBase.IsDestroyed),
    ];
    private readonly XRPropertyChangingEventHandler _childChanging;
    private readonly XRPropertyChangedEventHandler _childChanged;
    private Transform?[] _subscribedChildren = [];
    private Matrix4x4[] _rootRelativeMatrices = [];
    private Matrix4x4[] _captureMatrices = [];
    private int _subscribedCount;
    private int _count;
    private int _sourceVersion = -1;
    private long _ownershipGeneration;
    private long _invalidationGeneration = 1;
    private long _certifiedGeneration;
    private int _valid;
    private int _blocked;

    internal PhysicsChainRigidGpuRestInputCache()
    {
        _childChanging = XRPropertyNotificationHandlers.FilterChanging(OnChildChanging, ChildInputProperties);
        _childChanged = XRPropertyNotificationHandlers.FilterChanged(OnChildChanged, ChildInputProperties);
    }

    internal long InvalidationGeneration => Interlocked.Read(ref _invalidationGeneration);
    internal bool IsBlocked => Volatile.Read(ref _blocked) != 0;

    internal void Clear()
    {
        DiscardCertification();
        Volatile.Write(ref _blocked, 0);
    }

    private void DiscardCertification()
    {
        Volatile.Write(ref _valid, 0);
        Interlocked.Increment(ref _invalidationGeneration);
        for (int index = 0; index < _subscribedCount; index++)
        {
            _subscribedChildren[index]?.PropertyChanging -= _childChanging;
            _subscribedChildren[index]?.PropertyChanged -= _childChanged;
            _subscribedChildren[index] = null;
        }
        _subscribedCount = 0;
        _count = 0;
        _sourceVersion = -1;
    }

    private void OnChildChanging(object? sender, IXRPropertyChangingEventArgs args)
        => Block();

    private void OnChildChanged(object? sender, IXRPropertyChangedEventArgs args)
        => Block();

    private void Block()
    {
        Volatile.Write(ref _blocked, 1);
        Interlocked.Increment(ref _invalidationGeneration);
        Volatile.Write(ref _valid, 0);
    }

    internal bool TryCapture(PhysicsChainGpuRestInputRange range,
        Dictionary<TransformBase, int> resetNodeOwners,
        HashSet<TransformBase> prerequisiteMatrixTargets,
        long ownershipGeneration,
        bool sampled,
        out PhysicsChainRigidGpuRestCaptureSample sample)
    {
        sample = default;
        int stage = sampled ? 1 : 0;
        int rootCount = sampled ? 1 : 0;
        int expandCount = 0;
        int publishCount = 0;
        long rootTicks = 0L;
        long expandTicks = 0L;
        long publishTicks = 0L;
        long stageStart = sampled ? Stopwatch.GetTimestamp() : 0L;
        try
        {
            if (Volatile.Read(ref _blocked) != 0 || Volatile.Read(ref _valid) == 0 ||
                range.TreeCount != 1 ||
                range.Count != _count || range.SourceVersion != _sourceVersion ||
                ownershipGeneration != _ownershipGeneration || _count == 0 ||
                range.TreeOffsets[0] != 0 || range.Transforms[0] is not { } root ||
                root.GetType() != typeof(Transform) || root.ForceManualRecalc || root.IsDestroyed ||
                (resetNodeOwners.TryGetValue(root, out int ownerCount) && ownerCount > 1))
                return false;

            long generation = InvalidationGeneration;
            if (generation != Interlocked.Read(ref _certifiedGeneration))
                return false;
            TransformState state = root.FrameState;
            if (!PhysicsChainGpuRestInputRange.HasSameBits(in state.Rotation, in range.InitialRotations[0]))
                return false;

            for (TransformBase? ancestor = root.Parent; ancestor is not null; ancestor = ancestor.Parent)
                if (resetNodeOwners.ContainsKey(ancestor) || prerequisiteMatrixTargets.Contains(ancestor))
                    return false;

            Matrix4x4 parentWorld = root.Parent?.WorldMatrix ?? Matrix4x4.Identity;
            Matrix4x4 rootWorld = PhysicsChainRestTransformMatrix.Compose(
                state.Translation, range.InitialRotations[0], state.Scale, state.Order).ToMatrix4x4() * parentWorld;
            if (sampled)
            {
                long now = Stopwatch.GetTimestamp();
                rootTicks = now - stageStart;
                stageStart = now;
                stage = 2;
                expandCount = 1;
            }
            float maximumStretch = 0.0f;
            bool spatialValid = true;
            for (int index = 0; index < _count; index++)
            {
                Matrix4x4 matrix = _rootRelativeMatrices[index] * rootWorld;
                _captureMatrices[index] = matrix;
                Vector3 position = matrix.Translation;
                spatialValid &= float.IsFinite(position.X) && float.IsFinite(position.Y) &&
                    float.IsFinite(position.Z) && matrix.M14 == 0.0f && matrix.M24 == 0.0f &&
                    matrix.M34 == 0.0f && matrix.M44 == 1.0f;
                float stretch = PhysicsChainGpuSpatialInput.MaximumLinearStretch(in matrix);
                spatialValid &= float.IsFinite(stretch);
                maximumStretch = MathF.Max(maximumStretch, stretch);
            }

            if (sampled)
            {
                expandTicks = Stopwatch.GetTimestamp() - stageStart;
                stage = 0;
            }

            if (Volatile.Read(ref _blocked) != 0 || Volatile.Read(ref _valid) == 0 ||
                generation != InvalidationGeneration ||
                generation != Interlocked.Read(ref _certifiedGeneration))
                return false;

            if (sampled)
            {
                stageStart = Stopwatch.GetTimestamp();
                stage = 3;
                publishCount = 1;
            }
            bool changed = false;
            for (int index = 0; index < _count; index++)
            {
                if (range.Matrices[index] == _captureMatrices[index])
                    continue;
                range.Matrices[index] = _captureMatrices[index];
                changed = true;
            }
            if (changed)
                range.MatrixGeneration = checked(range.MatrixGeneration + 1);
            range.MaximumStretch = maximumStretch;
            range.SpatialValid = spatialValid;
            range.TreeAnchorMinimum[0] = rootWorld.Translation;
            range.TreeAnchorMaximum[0] = rootWorld.Translation;
            range.CachedCaptureGeneration = generation;
            if (sampled)
            {
                publishTicks = Stopwatch.GetTimestamp() - stageStart;
                stage = 0;
            }
            return true;
        }
        finally
        {
            if (sampled)
            {
                long now = Stopwatch.GetTimestamp();
                if (stage == 1)
                    rootTicks = now - stageStart;
                else if (stage == 2)
                    expandTicks = now - stageStart;
                else if (stage == 3)
                    publishTicks = now - stageStart;
                sample = new(rootTicks, rootCount, expandTicks, expandCount,
                    publishTicks, publishCount);
            }
        }
    }

    internal void Certify(PhysicsChainGpuRestInputRange range, long ownershipGeneration)
    {
        if (IsBlocked)
            return;
        DiscardCertification();
        if (IsBlocked)
            return;
        if (range.TreeCount != 1 || range.Count == 0 || range.TreeOffsets[0] != 0 ||
            range.Transforms[0] is not { } root || root.GetType() != typeof(Transform) ||
            root.ForceManualRecalc || root.IsDestroyed)
            return;

        EnsureCapacity(range.Count);
        for (int index = 1; index < range.Count; index++)
        {
            Transform? child = range.Transforms[index];
            if (child is not null &&
                (child.GetType() != typeof(Transform) || child.ForceManualRecalc || child.IsDestroyed))
                return;
        }
        for (int index = 1; index < range.Count; index++)
        {
            Transform? child = range.Transforms[index];
            if (child is null)
                continue;
            bool alreadySubscribed = false;
            for (int prior = 0; prior < _subscribedCount; prior++)
                alreadySubscribed |= ReferenceEquals(_subscribedChildren[prior], child);
            if (alreadySubscribed)
                continue;
            child.PropertyChanging += _childChanging;
            child.PropertyChanged += _childChanged;
            _subscribedChildren[_subscribedCount++] = child;
        }

        long generation = InvalidationGeneration;
        TransformState rootState = root.FrameState;
        if (!PhysicsChainGpuRestInputRange.HasSameBits(in rootState.Rotation, in range.InitialRotations[0]))
            return;
        _rootRelativeMatrices[0] = Matrix4x4.Identity;
        for (int index = 1; index < range.Count; index++)
        {
            int parent = range.ParentIndices[index];
            if ((uint)parent >= (uint)index)
                return;
            Transform? child = range.Transforms[index];
            if (child is null)
            {
                _rootRelativeMatrices[index] = _rootRelativeMatrices[parent];
                continue;
            }
            if (!ReferenceEquals(child.Parent, range.Transforms[parent]))
                return;
            TransformState state = child.FrameState;
            if (child.ForceManualRecalc ||
                !PhysicsChainGpuRestInputRange.HasSameBits(in state.Rotation, in range.InitialRotations[index]) ||
                !PhysicsChainGpuRestInputRange.HasSameBits(in state.Translation, in range.InitialTranslations[index]))
                return;
            Matrix4x4 local = PhysicsChainRestTransformMatrix.Compose(
                range.InitialTranslations[index], range.InitialRotations[index], state.Scale, state.Order).ToMatrix4x4();
            _rootRelativeMatrices[index] = local * _rootRelativeMatrices[parent];
        }
        if (IsBlocked || generation != InvalidationGeneration)
            return;
        _count = range.Count;
        _sourceVersion = range.SourceVersion;
        _ownershipGeneration = ownershipGeneration;
        Interlocked.Exchange(ref _certifiedGeneration, generation);
        Volatile.Write(ref _valid, 1);
        if (IsBlocked || generation != InvalidationGeneration)
            Volatile.Write(ref _valid, 0);
    }

    private void EnsureCapacity(int count)
    {
        if (_rootRelativeMatrices.Length >= count)
            return;
        int capacity = Math.Max(count, Math.Max(_rootRelativeMatrices.Length * 2, 8));
        Array.Resize(ref _rootRelativeMatrices, capacity);
        Array.Resize(ref _captureMatrices, capacity);
        Array.Resize(ref _subscribedChildren, capacity);
    }
}
