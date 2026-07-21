using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Scene;

public partial class VisualScene3D
{
    private const int MainVisibilityFamilyCacheCapacity = RenderFrameViewSet.MaxViewCount;
    private const int InitialMainVisibilityCandidateCapacity = 1024;
    private const int MaxMainVisibilityCandidateCapacity = 1 << 20;

    private readonly object _mainVisibilitySync = new();
    private readonly ViewBatchSplitHysteresis _mainVisibilityBatchSplitHysteresis =
        new(MainVisibilityFamilyCacheCapacity);
    private readonly MainVisibilityCacheEntry?[] _mainVisibilityFamilies =
        new MainVisibilityCacheEntry?[MainVisibilityFamilyCacheCapacity];
    private ulong _mainVisibilityFrameId = ulong.MaxValue;
    private MainVisibilityCacheEntry? _lastMainVisibilityCandidateEntry;

    public int LastFrameVisibilityCandidateCount { get; private set; }
    public bool LastFrameVisibilityCandidateOverflowed { get; private set; }
    public long VisibilityReferenceComparisons { get; private set; }
    public long VisibilityReferenceFalsePositives { get; private set; }
    public long VisibilityReferenceFalseNegatives { get; private set; }

    public bool TryGetLastFrameVisibilityCandidates(
        ulong frameId,
        out ReadOnlyMemory<FrameVisibilityCandidate> candidates)
    {
        lock (_mainVisibilitySync)
        {
            MainVisibilityCacheEntry? entry = _lastMainVisibilityCandidateEntry;
            int frameSlot = (int)(frameId % MainVisibilityCacheEntry.CandidateFrameSlotCount);
            if (entry is null ||
                entry.CandidateGenerations[frameSlot] != frameId ||
                entry.CandidateSlotOverflowed[frameSlot])
            {
                candidates = ReadOnlyMemory<FrameVisibilityCandidate>.Empty;
                return false;
            }

            candidates = new(
                entry.CandidateSlots[frameSlot],
                0,
                entry.CandidateCounts[frameSlot]);
            return true;
        }
    }

    internal bool TryCollectMaskedMainFamily(
        RenderCommandCollection commands,
        IVolume? collectionVolume,
        IRuntimeCullingCamera? camera,
        bool collectMirrors,
        XRRenderPipelineInstance.RenderingState state,
        out int visibleRenderables)
    {
        visibleRenderables = 0;
        if (_cpuSceneCullingStructureActive != ECpuSceneCullingStructure.Bvh ||
            IsGpuCulling ||
            commands.IsOwnedByShadowPipeline ||
            state.ShadowPass ||
            state.CapturePolicy.IsCapture ||
            state.WorldSnapshot is not RenderWorldSnapshot snapshot ||
            !ReferenceEquals(snapshot.Scene, this) ||
            state.FrameViewSet is not RenderFrameViewSet viewSet)
        {
            return false;
        }

        ulong familyKey = ComputeFamilyKey(viewSet, camera);
        MainVisibilityCacheEntry entry;
        lock (_mainVisibilitySync)
        {
            if (_mainVisibilityFrameId != snapshot.FrameId)
            {
                _mainVisibilityFrameId = snapshot.FrameId;
                for (int i = 0; i < _mainVisibilityFamilies.Length; i++)
                {
                    if (_mainVisibilityFamilies[i] is not MainVisibilityCacheEntry cached)
                        continue;
                    cached.Attempted = false;
                    cached.Built = false;
                    cached.Valid = false;
                }
            }

            entry = FindOrCreateMainVisibilityEntry(familyKey);
            EnsureMainVisibilityCapacity(entry);
            if (!entry.Attempted)
                BuildMainVisibilityEntry(entry, snapshot.FrameId, familyKey, viewSet);
        }

        if (!entry.Valid)
            return false;

        state.PublishVisibilityBatchDiagnostics(entry.SplitDecision, entry.ContentPolicy);
        ulong requestedViewMask = ResolveRequestedViewMask(viewSet, camera, collectionVolume is not null);
        bool modelDiagActive = ModelRenderDiagnostics.HasActiveTrace;
        for (int i = 0; i < entry.Results.Count; i++)
        {
            ref readonly CpuBvhMaskedResult<Rendering.Info.RenderInfo3D> candidate = ref entry.Results.Get(i);
            Rendering.Info.RenderInfo3D renderable = candidate.Item;
            ulong exactRequestedMask = candidate.ExactViewMask & requestedViewMask;
            if (exactRequestedMask == 0UL)
                continue;
            if (!renderable.AllowRender(null, commands, camera, false, collectMirrors))
            {
                if (modelDiagActive)
                    ModelRenderDiagnostics.LogRejected(renderable, collectionVolume, commands, camera, false, collectMirrors);
                continue;
            }

            exactRequestedMask = RefineExactViewMask(renderable, entry, exactRequestedMask);
            if (exactRequestedMask == 0UL)
            {
                if (modelDiagActive)
                    ModelRenderDiagnostics.LogRejected(renderable, collectionVolume, commands, camera, false, collectMirrors);
                continue;
            }

            visibleRenderables++;
            if (RenderDiagnosticsFlags.SkinCullRejectDiag)
            {
                renderable.DiagIntersectGen = _collectGen;
                renderable.DiagIntersectResult = true;
                renderable.DiagCollectedGen = _collectGen;
            }
            if (modelDiagActive)
                ModelRenderDiagnostics.LogVisibilityAccepted(renderable, commands, camera, collectMirrors);
            renderable.CollectCommands(commands, camera);
        }

        return true;
    }

    private MainVisibilityCacheEntry FindOrCreateMainVisibilityEntry(ulong familyKey)
    {
        int freeIndex = -1;
        for (int i = 0; i < _mainVisibilityFamilies.Length; i++)
        {
            MainVisibilityCacheEntry? entry = _mainVisibilityFamilies[i];
            if (entry is null)
            {
                if (freeIndex < 0)
                    freeIndex = i;
                continue;
            }
            if (entry.FamilyKey == familyKey)
                return entry;
            if (!entry.Valid && !entry.Attempted && freeIndex < 0)
                freeIndex = i;
        }

        if (freeIndex < 0)
            freeIndex = 0;
        return _mainVisibilityFamilies[freeIndex] = new(InitialMainVisibilityCandidateCapacity);
    }

    private void EnsureMainVisibilityCapacity(MainVisibilityCacheEntry entry)
    {
        int required = Math.Max(1, _renderables.Count);
        if (entry.Results.Capacity >= required)
            return;

        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)required);
        entry.Results = new CpuBvhMaskedResultBuffer<Rendering.Info.RenderInfo3D>(capacity);
    }

    private void BuildMainVisibilityEntry(
        MainVisibilityCacheEntry entry,
        ulong frameId,
        ulong familyKey,
        in RenderFrameViewSet viewSet)
    {
        entry.Attempted = true;
        entry.Built = false;
        entry.Valid = false;
        int viewCount = viewSet.ViewCount;
        for (int i = 0; i < viewCount; i++)
        {
            RenderFrameViewDescriptor descriptor = viewSet.GetView(i);
            UpdateWorldFrustum(entry.Frusta, i, descriptor.ViewProjectionMatrix, descriptor.DepthZeroToOne);
            int parentIndex = descriptor.HasParent ? checked((int)descriptor.ParentViewId) : -1;
            entry.Contexts[i] = new(entry.Frusta[i], parentIndex, descriptor.ParentContainsView);
        }

        entry.Results.BeginFrame();
        CpuBvhMaskedResultBuffer<Rendering.Info.RenderInfo3D>.Visitor visitor = entry.Results.CreateVisitor();
        _bvhRenderTree.CollectVisibleMasked(entry.Contexts.AsSpan(0, viewCount), ref visitor);
        entry.FrameId = frameId;
        entry.FamilyKey = familyKey;
        entry.ViewCount = viewCount;
        entry.ContentPolicy = ViewBatchContentPolicy.Exact;
        if (entry.Results.Overflowed)
        {
            entry.Similarity = default;
            entry.SplitDecision = default;
            int frameSlot = (int)(frameId % MainVisibilityCacheEntry.CandidateFrameSlotCount);
            entry.CandidateCounts[frameSlot] = 0;
            entry.CandidateGenerations[frameSlot] = frameId;
            entry.CandidateSlotOverflowed[frameSlot] = true;
            _lastMainVisibilityCandidateEntry = entry;
            LastFrameVisibilityCandidateCount = 0;
            LastFrameVisibilityCandidateOverflowed = true;
            RuntimeEngine.LogWarning(
                $"Exact main-view CPU BVH visibility overflowed capacity {entry.Results.Capacity}; " +
                "the family will use the existing per-output collector this frame.");
            return;
        }

        ulong activeViewMask = (1UL << viewCount) - 1UL;
        ViewBatchMaskSimilarityAccumulator similarityAccumulator = new(activeViewMask);
        for (int i = 0; i < entry.Results.Count; i++)
            similarityAccumulator.Add(entry.Results.Get(i).ExactViewMask);

        entry.Similarity = similarityAccumulator.Build();
        entry.SplitDecision = _mainVisibilityBatchSplitHysteresis.Evaluate(
            RenderFrameViewBatchPlanner.GetStructuralIdentity(viewSet, activeViewMask),
            entry.Similarity);
        if (!TryPopulateFrameVisibilityCandidates(entry, frameId))
        {
            entry.Valid = false;
            return;
        }
        if (ModelRenderDiagnostics.HasActiveTrace)
            CompareMaskedVisibilityWithReference(entry);
        entry.Built = true;
        entry.Valid = true;
    }

    private bool TryPopulateFrameVisibilityCandidates(MainVisibilityCacheEntry entry, ulong frameId)
    {
        int frameSlot = (int)(frameId % MainVisibilityCacheEntry.CandidateFrameSlotCount);
        int required = 0;
        for (int resultIndex = 0; resultIndex < entry.Results.Count; resultIndex++)
        {
            Rendering.Info.RenderInfo3D renderable = entry.Results.Get(resultIndex).Item;
            for (int commandIndex = 0; commandIndex < renderable.RenderCommands.Count; commandIndex++)
            {
                if (!renderable.RenderCommands[commandIndex].Enabled)
                    continue;
                if (required == MaxMainVisibilityCandidateCapacity)
                    return RejectCandidateOverflow(entry, frameId, required + 1);
                required++;
            }
        }

        if (entry.CandidateSlots[frameSlot].Length < required)
        {
            int capacity = entry.CandidateSlots[frameSlot].Length;
            while (capacity < required)
                capacity = Math.Min(capacity << 1, MaxMainVisibilityCandidateCapacity);
            entry.CandidateSlots[frameSlot] = new FrameVisibilityCandidate[capacity];
        }

        FrameVisibilityCandidate[] candidateSlot = entry.CandidateSlots[frameSlot];
        int candidateIndex = 0;
        for (int resultIndex = 0; resultIndex < entry.Results.Count; resultIndex++)
        {
            ref readonly CpuBvhMaskedResult<Rendering.Info.RenderInfo3D> result =
                ref entry.Results.Get(resultIndex);
            Rendering.Info.RenderInfo3D renderable = result.Item;
            ulong exactViewMask = RefineExactViewMask(renderable, entry, result.ExactViewMask);
            ulong renderBatchMembershipMask = entry.SplitDecision.Topology == EViewBatchTopology.SplitPerView
                ? exactViewMask
                : entry.Similarity.ActiveViewMask;
            if (exactViewMask == 0UL)
                continue;

            for (int commandIndex = 0; commandIndex < renderable.RenderCommands.Count; commandIndex++)
            {
                RenderCommand command = renderable.RenderCommands[commandIndex];
                if (!command.Enabled)
                    continue;

                int renderPass = command.RenderPass;
                ulong passEligibilityMask = (uint)renderPass < 64u
                    ? 1UL << renderPass
                    : 0UL;
                candidateSlot[candidateIndex++] = new(
                    renderable.StableInstanceId,
                    command.StableQueryKey,
                    passEligibilityMask,
                    exactViewMask,
                    exactViewMask,
                    renderBatchMembershipMask,
                    (uint)frameSlot,
                    frameId);
            }
        }

        entry.CandidateCounts[frameSlot] = candidateIndex;
        entry.CandidateGenerations[frameSlot] = frameId;
        entry.CandidateSlotOverflowed[frameSlot] = false;
        _lastMainVisibilityCandidateEntry = entry;
        LastFrameVisibilityCandidateCount = candidateIndex;
        LastFrameVisibilityCandidateOverflowed = false;
        return true;
    }

    private bool RejectCandidateOverflow(MainVisibilityCacheEntry entry, ulong frameId, int required)
    {
        int frameSlot = (int)(frameId % MainVisibilityCacheEntry.CandidateFrameSlotCount);
        entry.CandidateCounts[frameSlot] = 0;
        entry.CandidateGenerations[frameSlot] = frameId;
        entry.CandidateSlotOverflowed[frameSlot] = true;
        _lastMainVisibilityCandidateEntry = entry;
        LastFrameVisibilityCandidateCount = 0;
        LastFrameVisibilityCandidateOverflowed = true;
        RuntimeEngine.LogWarning(
            $"Exact main-view visibility requires at least {required} command candidates, exceeding " +
            $"the bounded capacity {MaxMainVisibilityCandidateCapacity}; overflow policy " +
            $"{EFrameVisibilityOverflowPolicy.RejectFrame} selects the existing per-output collector this frame.");
        return false;
    }

    private static ulong RefineExactViewMask(
        Rendering.Info.RenderInfo3D renderable,
        MainVisibilityCacheEntry entry,
        ulong exactViewMask)
    {
        if (renderable.CullingIntersectionOverride is null)
            return exactViewMask;

        ulong refinedViewMask = exactViewMask;
        ulong remainingMask = exactViewMask;
        while (remainingMask != 0UL)
        {
            int viewIndex = BitOperations.TrailingZeroCount(remainingMask);
            ulong viewBit = 1UL << viewIndex;
            if (!renderable.Intersects(entry.Frusta[viewIndex], false))
                refinedViewMask &= ~viewBit;
            remainingMask &= remainingMask - 1UL;
        }
        return refinedViewMask;
    }

    private void CompareMaskedVisibilityWithReference(MainVisibilityCacheEntry entry)
    {
        entry.ReferenceResults.EnsureCapacity(Math.Max(1, entry.Results.Count));
        long comparisons = 0;
        long falsePositives = 0;
        long falseNegatives = 0;
        for (int viewIndex = 0; viewIndex < entry.ViewCount; viewIndex++)
        {
            entry.ReferenceResults.Begin();
            CpuBvhReferenceResultBuffer<Rendering.Info.RenderInfo3D>.Visitor visitor =
                entry.ReferenceResults.CreateVisitor();
            _bvhRenderTree.CollectVisible(entry.Frusta[viewIndex], ref visitor);
            if (entry.ReferenceResults.Overflowed)
            {
                RuntimeEngine.LogWarning(
                    $"Exact visibility reference comparison overflowed capacity {entry.ReferenceResults.Capacity}; " +
                    $"view {viewIndex} was not compared.");
                continue;
            }

            ulong viewBit = 1UL << viewIndex;
            for (int referenceIndex = 0; referenceIndex < entry.ReferenceResults.Count; referenceIndex++)
            {
                Rendering.Info.RenderInfo3D reference = entry.ReferenceResults.Get(referenceIndex);
                bool exactContainsReference = false;
                for (int resultIndex = 0; resultIndex < entry.Results.Count; resultIndex++)
                {
                    ref readonly CpuBvhMaskedResult<Rendering.Info.RenderInfo3D> result =
                        ref entry.Results.Get(resultIndex);
                    if (ReferenceEquals(result.Item, reference) && (result.ExactViewMask & viewBit) != 0UL)
                    {
                        exactContainsReference = true;
                        break;
                    }
                }

                comparisons++;
                if (!exactContainsReference)
                    falseNegatives++;
            }

            for (int resultIndex = 0; resultIndex < entry.Results.Count; resultIndex++)
            {
                ref readonly CpuBvhMaskedResult<Rendering.Info.RenderInfo3D> result =
                    ref entry.Results.Get(resultIndex);
                if ((result.ExactViewMask & viewBit) == 0UL)
                    continue;

                comparisons++;
                if (!entry.ReferenceResults.ContainsReference(result.Item))
                    falsePositives++;
            }
        }

        entry.ReferenceComparisons += comparisons;
        entry.ReferenceFalsePositives += falsePositives;
        entry.ReferenceFalseNegatives += falseNegatives;
        VisibilityReferenceComparisons = entry.ReferenceComparisons;
        VisibilityReferenceFalsePositives = entry.ReferenceFalsePositives;
        VisibilityReferenceFalseNegatives = entry.ReferenceFalseNegatives;
        if (falsePositives != 0 || falseNegatives != 0)
        {
            RuntimeEngine.LogWarning(
                $"Exact visibility diagnostic mismatch for family 0x{entry.FamilyKey:X16}: " +
                $"falsePositives={falsePositives}, falseNegatives={falseNegatives}, comparisons={comparisons}.");
        }
    }

    private static void UpdateWorldFrustum(
        Frustum[] frusta,
        int index,
        Matrix4x4 viewProjection,
        bool depthZeroToOne)
    {
        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse))
            throw new InvalidOperationException("A captured frame view-projection matrix is not invertible.");

        float nearZ = depthZeroToOne ? 0.0f : -1.0f;
        frusta[index].UpdatePoints(
            Unproject(-1.0f, -1.0f, nearZ, inverse),
            Unproject(1.0f, -1.0f, nearZ, inverse),
            Unproject(-1.0f, 1.0f, nearZ, inverse),
            Unproject(1.0f, 1.0f, nearZ, inverse),
            Unproject(-1.0f, -1.0f, 1.0f, inverse),
            Unproject(1.0f, -1.0f, 1.0f, inverse),
            Unproject(-1.0f, 1.0f, 1.0f, inverse),
            Unproject(1.0f, 1.0f, 1.0f, inverse));
    }

    private static Vector3 Unproject(float x, float y, float z, in Matrix4x4 inverse)
    {
        Vector4 value = Vector4.Transform(new Vector4(x, y, z, 1.0f), inverse);
        return new(value.X / value.W, value.Y / value.W, value.Z / value.W);
    }

    private static ulong ResolveRequestedViewMask(
        in RenderFrameViewSet viewSet,
        IRuntimeCullingCamera? camera,
        bool hasCollectionVolumeOverride)
    {
        ulong allViews = (1UL << viewSet.ViewCount) - 1UL;
        if (hasCollectionVolumeOverride || camera is not XRCamera xrCamera || xrCamera.StereoEyeLeft is null)
            return allViews;

        bool left = xrCamera.StereoEyeLeft.Value;
        ulong mask = 0UL;
        for (int i = 0; i < viewSet.ViewCount; i++)
        {
            RenderFrameViewDescriptor view = viewSet.GetView(i);
            if ((left && view.IsLeftEyeFamily) || (!left && view.IsRightEyeFamily))
                mask |= 1UL << i;
        }
        return mask != 0UL ? mask : allViews;
    }

    private static ulong ComputeFamilyKey(in RenderFrameViewSet viewSet, IRuntimeCullingCamera? camera)
    {
        HashCode hash = new();
        hash.Add(viewSet.ViewCount);
        hash.Add(viewSet.VisibilityGroupCount);
        bool stereoFamily = false;
        for (int i = 0; i < viewSet.ViewCount; i++)
        {
            if (viewSet.GetView(i).IsXrSubmittedView)
            {
                stereoFamily = viewSet.ViewCount > 1;
                break;
            }
        }
        hash.Add(stereoFamily ? 0 : RuntimeHelpers.GetHashCode(camera!));
        for (int i = 0; i < viewSet.ViewCount; i++)
        {
            RenderFrameViewDescriptor view = viewSet.GetView(i);
            hash.Add(view.EffectiveHistoryKey);
            hash.Add(view.VisibilityGroupIndex);
            hash.Add(view.Kind);
        }
        return unchecked((ulong)hash.ToHashCode()) | 1UL;
    }

    private sealed class MainVisibilityCacheEntry
    {
        public MainVisibilityCacheEntry(int candidateCapacity)
        {
            Results = new(candidateCapacity);
            for (int i = 0; i < Frusta.Length; i++)
                Frusta[i] = new Frustum();
        }

        public CpuBvhMaskedResultBuffer<Rendering.Info.RenderInfo3D> Results;
        public readonly Frustum[] Frusta = new Frustum[RenderFrameViewSet.MaxViewCount];
        public readonly CpuBvhFrustum[] Contexts = new CpuBvhFrustum[RenderFrameViewSet.MaxViewCount];
        public ulong FrameId;
        public ulong FamilyKey;
        public int ViewCount;
        public const int CandidateFrameSlotCount = 3;
        public readonly FrameVisibilityCandidate[][] CandidateSlots =
        [
            new FrameVisibilityCandidate[InitialMainVisibilityCandidateCapacity],
            new FrameVisibilityCandidate[InitialMainVisibilityCandidateCapacity],
            new FrameVisibilityCandidate[InitialMainVisibilityCandidateCapacity],
        ];
        public readonly int[] CandidateCounts = new int[CandidateFrameSlotCount];
        public readonly ulong[] CandidateGenerations = new ulong[CandidateFrameSlotCount];
        public readonly bool[] CandidateSlotOverflowed = new bool[CandidateFrameSlotCount];
        public readonly CpuBvhReferenceResultBuffer<Rendering.Info.RenderInfo3D> ReferenceResults =
            new(InitialMainVisibilityCandidateCapacity);
        public long ReferenceComparisons;
        public long ReferenceFalsePositives;
        public long ReferenceFalseNegatives;
        public ViewBatchMaskSimilarity Similarity;
        public ViewBatchSplitDecision SplitDecision;
        public ViewBatchContentPolicy ContentPolicy = ViewBatchContentPolicy.Exact;
        public bool Attempted;
        public bool Built;
        public bool Valid;
    }
}
