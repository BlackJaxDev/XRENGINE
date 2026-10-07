using XREngine.Data.Rendering;
using XREngine.Data.Geometry;
using XREngine.Components;

namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private const int OutputPageCount = 4;
    private readonly object _outputPageSync = new();
    private PhysicsChainOutputPage[] _outputPages =
    [
        new(), new(), new(), new(),
    ];
    private readonly List<RetiredOutputPage> _retiredOutputPages = [];
    private readonly Dictionary<XRMeshRenderer, PhysicsChainGpuBoundsSource> _committedBoundsSources =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly Dictionary<XRMeshRenderer, PhysicsChainGpuBoundsSource> _pendingBoundsSources =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly List<GPUPhysicsChainRequest> _outputPublicationRequests = [];
    private readonly Dictionary<uint, (uint SourceBase, uint Count)> _copiedPreviousPaletteSlices = [];
    private readonly HashSet<uint> _seededPreviousPaletteSlices = [];
    private XRDataBuffer<SkinPaletteMatrix>? _unpagedSkinPaletteBuffer;
    private XRDataBuffer<SkinPaletteMatrix>? _unpagedPreviousSkinPaletteBuffer;
    private int _publishedOutputPageIndex = -1;
    private int _historyOutputPageIndex = -1;
    private int _writingOutputPageIndex = -1;
    private uint _outputProducerEpoch;
    private uint _boundsSlotGenerationOrdinal;
    private long _outputPageBusyCount;
    private long _outputPageReuseCount;
    private long _outputPageFailureCount;

    /// <summary>Reports page reuse pressure without reading GPU memory.</summary>
    public PhysicsChainGpuOutputPageDiagnostics OutputPageDiagnostics
    {
        get
        {
            lock (_outputPageSync)
                return new(_outputPageBusyCount, _outputPageReuseCount,
                    _outputPageFailureCount, _publishedOutputPageIndex >= 0,
                    _outputProducerEpoch);
        }
    }

    /// <summary>Captures page blockers without polling a producer fence.</summary>
    public static PhysicsChainGpuOutputPageState[] CaptureOutputPageStates()
        => Instance.GetOutputPageStatesSnapshot();

    /// <summary>Captures page blockers for this dispatcher.</summary>
    public PhysicsChainGpuOutputPageState[] GetOutputPageStatesSnapshot()
    {
        lock (_outputPageSync)
        {
            var states = new PhysicsChainGpuOutputPageState[
                _outputPages.Length + _retiredOutputPages.Count];
            for (int index = 0; index < _outputPages.Length; ++index)
                states[index] = CaptureOutputPageState(index, _outputPages[index], retired: false);
            for (int index = 0; index < _retiredOutputPages.Count; ++index)
            {
                RetiredOutputPage retired = _retiredOutputPages[index];
                states[_outputPages.Length + index] = CaptureOutputPageState(
                    retired.Index, retired.Page, retired: true);
            }
            return states;
        }
    }

    private PhysicsChainGpuOutputPageState CaptureOutputPageState(
        int index, PhysicsChainOutputPage page, bool retired)
    {
        IGpuBufferContentReuseCapability? reuse = null;
        bool producerIsCurrent = page.ProducerRenderer is not null
            && ReferenceEquals(page.ProducerRenderer, AbstractRenderer.Current);
        bool hasCapability = producerIsCurrent
            && page.ProducerRenderer is IRuntimeRendererHost host
            && host.TryGetBackendCapability(out reuse)
            && reuse is not null;
        return new(
            index, page.Generation, page.ProducerEpoch, retired, page.Published,
            !retired && index == _publishedOutputPageIndex,
            !retired && index == _historyOutputPageIndex,
            !retired && index == _writingOutputPageIndex,
            page.RetainCount, page.HasQueuedGpuWork, page.HasUnfencedGpuWork,
            producerIsCurrent, page.ProducerRenderer?.GetType().Name,
            page.ProducerFence?.SubmissionStatus,
            FenceIsSignaled: null,
            hasCapability,
            GetReuseStatus(page.BoundsAtlas),
            GetReuseStatus(page.SlotMetadata),
            GetReuseStatus(page.CurrentPalette),
            GetReuseStatus(page.PreviousPalette));

        EGpuBufferContentReuseStatus? GetReuseStatus(XRDataBuffer? buffer)
            => buffer is null || reuse is null ? null : reuse.QueryBufferContentReuse(buffer);
    }

    /// <summary>Captures one committed renderer slot for a later output page.</summary>
    public bool TryCaptureGpuBoundsSource(XRMeshRenderer renderer, out PhysicsChainGpuBoundsSource source)
    {
        lock (_outputPageSync)
        {
            InvalidateFailedPublishedPages();
            if (_committedBoundsSources.TryGetValue(renderer, out source)
                && renderer.HasCompleteGpuDrivenBoneCoverage
                && renderer.BoneBufferGeneration == source.RendererBoneBufferGeneration)
                return true;
            source = default;
            return false;
        }
    }

    /// <summary>Retains the latest committed output page for one renderer frame.</summary>
    public bool TryAcquirePublishedOutputPage(out PhysicsChainGpuOutputPageLease lease)
    {
        lock (_outputPageSync)
        {
            InvalidateFailedPublishedPages();
            if (_publishedOutputPageIndex < 0)
            {
                lease = default;
                return false;
            }

            return TryRetainOutputPageLocked(_publishedOutputPageIndex,
                MakeOutputPageToken(_publishedOutputPageIndex), out lease);
        }
    }

    /// <summary>Retains a page while a later GPU pass consumes its buffers.</summary>
    public bool TryRetainOutputPage(PhysicsChainGpuOutputPageToken token,
        out PhysicsChainGpuOutputPageLease lease)
    {
        lock (_outputPageSync)
            return TryRetainOutputPageLocked(ToOutputPageIndex(token), token, out lease);
    }

    /// <summary>Checks a retained page without changing its retain count.</summary>
    public bool TryValidateOutputPage(PhysicsChainGpuOutputPageToken token,
        out PhysicsChainGpuOutputPageLease lease)
    {
        lock (_outputPageSync)
        {
            int index = ToOutputPageIndex(token);
            if (!TryGetOutputPageLocked(index, token, out PhysicsChainOutputPage? page)
                || !IsUsableOutputPage(page!))
            {
                lease = default;
                return false;
            }

            lease = MakeOutputPageLease(index, page!);
            return true;
        }
    }

    /// <summary>Captures inputs for a covered draw. The caller must retain the page while it uses the span.</summary>
    public bool TryCaptureRendererOutputState(PhysicsChainGpuOutputPageToken token,
        scoped in PhysicsChainGpuBoundsSource source, XRMesh mesh,
        out PhysicsChainGpuRendererOutputState state,
        out ReadOnlySpan<PhysicsChainMorphWeight> morphWeights)
    {
        morphWeights = default;
        lock (_outputPageSync)
        {
            if (!ReferenceEquals(source.Dispatcher, this) ||
                !TryGetOutputPageLocked(ToOutputPageIndex(token), token, out PhysicsChainOutputPage? page) ||
                !IsUsableOutputPage(page!) ||
                !page!.RendererStates.TryGetValue(source.Renderer, out state) ||
                state.BoundsSource != source ||
                source.Renderer.BoneBufferGeneration != source.RendererBoneBufferGeneration ||
                !state.Envelope.Matches(source.Renderer, mesh))
            {
                state = default;
                return false;
            }
            morphWeights = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(page!.MorphWeights)
                .Slice(state.MorphWeightOffset, state.MorphWeightCount);
            return true;
        }
    }

    /// <summary>Releases one retained page token.</summary>
    public void ReleaseOutputPage(PhysicsChainGpuOutputPageToken token)
    {
        lock (_outputPageSync)
        {
            int index = ToOutputPageIndex(token);
            if (TryGetOutputPageLocked(index, token, out PhysicsChainOutputPage? page))
            {
                if (page!.RetainCount > 0)
                    --page.RetainCount;
                return;
            }

            for (int retiredIndex = 0; retiredIndex < _retiredOutputPages.Count; ++retiredIndex)
            {
                RetiredOutputPage retired = _retiredOutputPages[retiredIndex];
                if (retired.Index != index || retired.Page.Generation != token.PageGeneration
                    || retired.Page.ProducerEpoch != token.ProducerEpoch)
                    continue;
                if (retired.Page.RetainCount > 0)
                    --retired.Page.RetainCount;
                if (retired.Page.RetainCount == 0 && retired.Page.NativeResourcesRetired)
                {
                    DisposeOutputPageResources(retired.Page);
                    _retiredOutputPages.RemoveAt(retiredIndex);
                }
                return;
            }
        }
    }

    /// <summary>Releases retired pages after the producer renderer is known idle.</summary>
    public void NotifyOutputProducerKnownIdle(AbstractRenderer renderer)
    {
        if (!RuntimeEngine.IsRenderThread)
            return;
        if (ReferenceEquals(_evaluatedRenderer, renderer))
        {
            RetireSimulationReceipts();
            RetireInputPages();
            RetireOutputPages();
        }
        NotifyInputProducerKnownIdle(renderer);
        lock (_outputPageSync)
        {
            for (int index = _retiredOutputPages.Count - 1; index >= 0; --index)
            {
                PhysicsChainOutputPage page = _retiredOutputPages[index].Page;
                if (!ReferenceEquals(page.ProducerRenderer, renderer))
                    continue;
                page.KnownIdle = true;
                page.NativeResourcesRetired = RetireOutputPageNativeResources(page);
                if (!page.NativeResourcesRetired)
                {
                    ++_outputPageFailureCount;
                    continue;
                }
                if (page.RetainCount != 0)
                    continue;
                DisposeOutputPageResources(page);
                _retiredOutputPages.RemoveAt(index);
            }
        }
    }

    private void RetireOutputPages()
    {
        lock (_outputPageSync)
        {
            PhysicsChainOutputPage? prior = _publishedOutputPageIndex >= 0
                ? _outputPages[_publishedOutputPageIndex] : null;
            for (int index = 0; index < _outputPages.Length; ++index)
            {
                PhysicsChainOutputPage page = _outputPages[index];
                if (page.ProducerRenderer is not null || page.BoundsAtlas is not null
                    || page.SlotMetadata is not null || page.RetainCount != 0)
                {
                    page.DisposeRequested = true;
                    _retiredOutputPages.Add(new(index, page));
                }
            }
            _outputPages = [new(), new(), new(), new()];
            _publishedOutputPageIndex = -1;
            _historyOutputPageIndex = -1;
            _writingOutputPageIndex = -1;
            _committedBoundsSources.Clear();
            _pendingBoundsSources.Clear();
            _pendingPaletteInputBoundsByRenderer.Clear();
            _lastPublishedPaletteInputBoundsByRenderer.Clear();
            _gpuBoundsAtlasBuffer = null;
            _gpuBoundsSlotMetadataBuffer = null;
            _gpuDrivenSkinPaletteBuffer = null;
            _gpuDrivenPreviousSkinPaletteBuffer = null;
            if (prior is not null)
                NotifyCommittedSpatialBoundsChanged(prior, published: false);
        }
    }

    private bool TryRetainOutputPageLocked(int index, PhysicsChainGpuOutputPageToken token,
        out PhysicsChainGpuOutputPageLease lease)
    {
        if (!TryGetOutputPageLocked(index, token, out PhysicsChainOutputPage? page)
            || !IsUsableOutputPage(page!)
            || page!.RetainCount == int.MaxValue)
        {
            lease = default;
            return false;
        }

        ++page.RetainCount;
        lease = MakeOutputPageLease(index, page);
        return true;
    }

    private static int ToOutputPageIndex(PhysicsChainGpuOutputPageToken token)
        => token.PageIndexPlusOne is > 0u and <= OutputPageCount
            ? (int)token.PageIndexPlusOne - 1 : -1;

    private bool TryGetOutputPageLocked(int index, PhysicsChainGpuOutputPageToken token,
        out PhysicsChainOutputPage? page)
    {
        page = index >= 0 && index < _outputPages.Length ? _outputPages[index] : null;
        return token.IsValid && page is { Published: true }
            && page.Generation == token.PageGeneration
            && page.ProducerEpoch == token.ProducerEpoch
            && page.BoundsAtlas is not null
            && page.SlotMetadata is not null;
    }

    private static bool IsUsableOutputPage(PhysicsChainOutputPage page)
        => !page.HasUnfencedGpuWork
            && !page.KnownProducerFailure
            && page.ProducerFence is { IsDisposed: false }
            && page.ProducerFence?.SubmissionStatus != EGpuFenceSubmissionStatus.Failed
            && page.ProducerRenderer is { AcceptsBackendWork: true, IsDeviceLost: false };

    private PhysicsChainGpuOutputPageToken MakeOutputPageToken(int index)
    {
        PhysicsChainOutputPage page = _outputPages[index];
        return new(checked((uint)index + 1u), page.Generation, page.ProducerEpoch);
    }

    private PhysicsChainGpuOutputPageLease MakeOutputPageLease(int index, PhysicsChainOutputPage page)
        => new(MakeOutputPageToken(index), page.BoundsAtlas!, page.SlotMetadata!,
            page.CurrentPalette, page.PreviousPalette, page.ProducerEpoch,
            page.Generation, page.RenderFrame)
        {
            PreviousPaletteSourceToken = page.PreviousPaletteSourceToken,
        };

    private bool TryBeginOutputPage(IPhysicsChainComputeBackend backend)
    {
        lock (_outputPageSync)
        {
            InvalidateFailedPublishedPages();
            SweepRetiredOutputPages();
            if (_writingOutputPageIndex >= 0 || _outputProducerEpoch == uint.MaxValue)
            {
                ++_outputPageFailureCount;
                return false;
            }

            for (int index = 0; index < _outputPages.Length; ++index)
            {
                if (index == _publishedOutputPageIndex || index == _historyOutputPageIndex)
                    continue;

                PhysicsChainOutputPage page = _outputPages[index];
                if (page.RetainCount != 0 || !CanReuseOutputPage(backend, page))
                    continue;
                if (page.Generation == uint.MaxValue)
                {
                    ++_outputPageFailureCount;
                    return false;
                }

                page.Generation++;
                page.Published = false;
                page.HasQueuedGpuWork = false;
                page.KnownProducerFailure = false;
                page.FailureRecoveryRequested = false;
                page.PaletteSlices.Clear();
                page.PaletteHistoryResetRenderers.Clear();
                page.RendererStates.Clear();
                page.MorphWeights.Clear();
                page.SpatialCheckpoints.Clear();
                page.PreviousPaletteSourceToken = default;
                page.ProducerRenderer = backend.Renderer;
                _writingOutputPageIndex = index;
                ++_outputPageReuseCount;
                return true;
            }

            ++_outputPageBusyCount;
            return false;
        }
    }

    private static bool CanReuseOutputPage(IPhysicsChainComputeBackend backend, PhysicsChainOutputPage page)
    {
        if (page.ProducerRenderer is not null
            && !ReferenceEquals(page.ProducerRenderer, backend.Renderer))
            return false;
        bool uncertainProducer = page.HasUnfencedGpuWork || page.KnownProducerFailure;
        if (page.ProducerFence is not null)
        {
            EGpuFenceSubmissionStatus status = page.ProducerFence.SubmissionStatus;
            if (status == EGpuFenceSubmissionStatus.AwaitingSubmission)
                return false;
            if (status == EGpuFenceSubmissionStatus.Failed)
                uncertainProducer = true;
            else if (page.ProducerFence.Poll() != EGpuFenceStatus.Signaled)
                return false;
        }

        if (backend.Renderer is IRuntimeRendererHost host && host.BackendId == RendererBackendId.Vulkan)
        {
            if (uncertainProducer && (host.IsDeviceLost || !backend.Renderer.AcceptsBackendWork))
                return false;
            if (!host.TryGetBackendCapability<IGpuBufferContentReuseCapability>(out var capability)
                || capability is null)
                return false;
            if (!IsNativeReuseReady(capability, page.BoundsAtlas)
                || !IsNativeReuseReady(capability, page.SlotMetadata)
                || !IsNativeReuseReady(capability, page.CurrentPalette)
                || !IsNativeReuseReady(capability, page.PreviousPalette))
                return false;
        }
        else if (uncertainProducer)
            return false;

        page.ProducerFence?.Dispose();
        page.ProducerFence = null;
        page.HasUnfencedGpuWork = false;
        return true;
    }

    private static bool IsNativeReuseReady(IGpuBufferContentReuseCapability capability,
        XRDataBuffer? buffer)
    {
        if (buffer is null)
            return true;
        XRBufferStateSnapshot state = buffer.GetStateSnapshot();
        // Client storage can have bytes before any native buffer exists.
        if (!state.IsApiObjectGenerated && state.UploadedByteCount == 0u)
            return true;
        return capability.QueryBufferContentReuse(buffer) == EGpuBufferContentReuseStatus.Ready;
    }

    private void InvalidateFailedPublishedPages()
    {
        foreach (PhysicsChainOutputPage page in _outputPages)
        {
            if (!page.KnownProducerFailure &&
                page.ProducerFence?.SubmissionStatus == EGpuFenceSubmissionStatus.Failed)
            {
                page.KnownProducerFailure = true;
                ++_outputPageFailureCount;
            }
            if (!page.KnownProducerFailure || page.FailureRecoveryRequested)
                continue;
            page.FailureRecoveryRequested = true;
            foreach (GPUPhysicsChainRequest request in page.SpatialCheckpoints.Keys)
                request.RetryAcceptedInput();
            _paletteOutputRefreshPending = true;
        }
        if (_publishedOutputPageIndex >= 0
            && !IsUsableOutputPage(_outputPages[_publishedOutputPageIndex]))
        {
            PhysicsChainOutputPage failed = _outputPages[_publishedOutputPageIndex];
            _publishedOutputPageIndex = -1;
            _committedBoundsSources.Clear();
            _lastPublishedPaletteInputBoundsByRenderer.Clear();
            _paletteOutputRefreshPending = true;
            NotifyCommittedSpatialBoundsChanged(failed, published: false);
        }
        if (_historyOutputPageIndex >= 0
            && !IsUsableOutputPage(_outputPages[_historyOutputPageIndex]))
            _historyOutputPageIndex = -1;
    }

    private bool CommitOutputPage(IPhysicsChainComputeBackend backend)
    {
        lock (_outputPageSync)
        {
            if (_writingOutputPageIndex < 0)
                return false;

            PhysicsChainOutputPage page = _outputPages[_writingOutputPageIndex];
            if (page.BoundsAtlas is null || page.SlotMetadata is null
                || _pendingBoundsSources.Count == 0)
            {
                page.HasUnfencedGpuWork = page.HasQueuedGpuWork;
                ++_outputPageFailureCount;
                _writingOutputPageIndex = -1;
                return false;
            }
            try
            {
                page.ProducerFence = backend.InsertFence();
            }
            catch (Exception exception)
            {
                XREngine.Debug.LogException(exception);
                page.HasUnfencedGpuWork = true;
                ++_outputPageFailureCount;
                _writingOutputPageIndex = -1;
                return false;
            }
            if (page.ProducerFence is null
                || page.ProducerFence.SubmissionStatus == EGpuFenceSubmissionStatus.Failed)
            {
                page.HasUnfencedGpuWork = true;
                page.KnownProducerFailure = page.ProducerFence?.SubmissionStatus == EGpuFenceSubmissionStatus.Failed;
                ++_outputPageFailureCount;
                _writingOutputPageIndex = -1;
                return false;
            }

            page.ProducerEpoch = checked(_outputProducerEpoch + 1u);
            page.ProducerRenderer = backend.Renderer;
            _outputProducerEpoch = page.ProducerEpoch;
            page.RenderFrame = _readbackFrameIndex;
            page.Published = true;
            _historyOutputPageIndex = _publishedOutputPageIndex;
            _publishedOutputPageIndex = _writingOutputPageIndex;
            _writingOutputPageIndex = -1;
            _committedBoundsSources.Clear();
            foreach (KeyValuePair<XRMeshRenderer, PhysicsChainGpuBoundsSource> entry in _pendingBoundsSources)
                _committedBoundsSources.Add(entry.Key, entry.Value);
            _lastPublishedPaletteInputBoundsByRenderer.Clear();
            foreach (KeyValuePair<XRMeshRenderer, (AABB Bounds, long BoneGeneration)> entry in _pendingPaletteInputBoundsByRenderer)
                _lastPublishedPaletteInputBoundsByRenderer.Add(entry.Key, entry.Value);
            NotifyCommittedSpatialBoundsChanged(page, published: true);
            return true;
        }
    }

    private bool CopyPreviousOutputPalette(IPhysicsChainComputeBackend backend,
        PhysicsChainOutputPage destination)
    {
        if (destination.CurrentPalette is null || destination.PreviousPalette is null)
            return false;

        destination.PaletteSlices.Clear();
        destination.PaletteHistoryResetRenderers.Clear();
        _copiedPreviousPaletteSlices.Clear();
        _seededPreviousPaletteSlices.Clear();
        PhysicsChainOutputPage? source = _publishedOutputPageIndex >= 0
            ? _outputPages[_publishedOutputPageIndex] : null;
        XRDataBuffer<SkinPaletteMatrix>? sourcePalette = source?.CurrentPalette;
        destination.PreviousPaletteSourceToken = sourcePalette is not null
            ? MakeOutputPageToken(_publishedOutputPageIndex) : default;
        XRDataBuffer<SkinPaletteMatrix> previousPalette = destination.PreviousPalette;
        uint sourceBase = 0u;
        uint destinationBase = 0u;
        uint copyCount = 0u;
        bool copiedAny = false;

        for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
        {
            GpuDrivenRendererPaletteBinding binding = _gpuDrivenPaletteBindings[bindingIndex];
            uint baseElement = _gpuDrivenPaletteSliceBases[bindingIndex];
            uint count = Math.Max(binding.BoneMatrixElementCount, 1u);
            var catalogEntry = (Base: baseElement, Count: count,
                BoneGeneration: binding.RendererBoneBufferGeneration);
            if (destination.PaletteSlices.TryGetValue(binding.Renderer, out var existing))
            {
                if (existing != catalogEntry)
                    return false;
            }
            else
                destination.PaletteSlices.Add(binding.Renderer, catalogEntry);

            (uint Base, uint Count, long BoneGeneration) prior = default;
            bool matched = sourcePalette is not null
                && IsUsableOutputPage(source!)
                && source!.PaletteSlices.TryGetValue(binding.Renderer, out prior)
                && prior.Count == count
                && prior.BoneGeneration == binding.RendererBoneBufferGeneration
                && prior.Base < sourcePalette.ElementCount
                && count <= sourcePalette.ElementCount - prior.Base
                && _outputPublicationRequestByComponent.TryGetValue(binding.Component, out var request)
                && source.RendererStates.TryGetValue(binding.Renderer, out var priorState)
                && priorState.SpatialSource == GetSpatialSourceIdentity(request, in binding)
                && ReferenceEquals(priorState.Envelope.Mesh, binding.Renderer.Mesh)
                && priorState.Envelope.GeometryRevision == binding.Renderer.Mesh?.GeometryRevision
                && priorState.Envelope.BindRoot == binding.Renderer.Mesh?.BindRootMatrix
                && ReferenceEquals(priorState.Envelope.BoneLayout, binding.Renderer.Mesh?.UtilizedBones);
            if (!matched)
            {
                destination.PaletteHistoryResetRenderers.Add(binding.Renderer);
                continue;
            }

            if (_copiedPreviousPaletteSlices.TryGetValue(baseElement, out var selected))
            {
                if (selected.SourceBase != prior.Base || selected.Count != count)
                    destination.PaletteHistoryResetRenderers.Add(binding.Renderer);
                continue;
            }
            _copiedPreviousPaletteSlices.Add(baseElement, (prior.Base, count));
            if (baseElement >= previousPalette.ElementCount
                || count > previousPalette.ElementCount - baseElement)
                return false;

            if (copyCount != 0u
                && sourceBase + copyCount == prior.Base
                && destinationBase + copyCount == baseElement)
            {
                copyCount = checked(copyCount + count);
                continue;
            }
            if (copyCount != 0u)
            {
                if (!TryCopyPreviousPaletteRange(backend, sourcePalette!, sourceBase,
                    previousPalette, destinationBase, copyCount))
                    return false;
                copiedAny = true;
            }
            sourceBase = prior.Base;
            destinationBase = baseElement;
            copyCount = count;
        }

        if (copyCount != 0u)
        {
            if (!TryCopyPreviousPaletteRange(backend, sourcePalette!, sourceBase,
                previousPalette, destinationBase, copyCount))
                return false;
            copiedAny = true;
        }

        sourceBase = 0u;
        destinationBase = 0u;
        copyCount = 0u;
        for (int bindingIndex = 0; bindingIndex < _gpuDrivenPaletteBindings.Count; ++bindingIndex)
        {
            uint baseElement = _gpuDrivenPaletteSliceBases[bindingIndex];
            if (_copiedPreviousPaletteSlices.ContainsKey(baseElement)
                || !_seededPreviousPaletteSlices.Add(baseElement))
                continue;

            uint count = Math.Max(_gpuDrivenPaletteBindings[bindingIndex].BoneMatrixElementCount, 1u);
            if (baseElement >= destination.CurrentPalette.ElementCount
                || count > destination.CurrentPalette.ElementCount - baseElement
                || baseElement >= previousPalette.ElementCount
                || count > previousPalette.ElementCount - baseElement)
                return false;
            if (copyCount != 0u && sourceBase + copyCount == baseElement
                && destinationBase + copyCount == baseElement)
            {
                copyCount = checked(copyCount + count);
                continue;
            }
            if (copyCount != 0u)
            {
                if (!TryCopyPreviousPaletteRange(backend, destination.CurrentPalette,
                    sourceBase, previousPalette, destinationBase, copyCount))
                    return false;
                copiedAny = true;
            }
            sourceBase = baseElement;
            destinationBase = baseElement;
            copyCount = count;
        }

        if (copyCount != 0u)
        {
            if (!TryCopyPreviousPaletteRange(backend, destination.CurrentPalette,
                sourceBase, previousPalette, destinationBase, copyCount))
                return false;
            copiedAny = true;
        }
        return !copiedAny || TryCompletePass(backend, PartialPaletteSeedCompletionPass);
    }

    private bool TryCopyPreviousPaletteRange(IPhysicsChainComputeBackend backend,
        XRDataBuffer<SkinPaletteMatrix> source, uint sourceBase,
        XRDataBuffer<SkinPaletteMatrix> destination, uint destinationBase, uint count)
    {
        if (!backend.EnsureGpuBufferReady(source)
            || !backend.EnsureGpuBufferReady(destination))
            return false;
        nuint stride = (nuint)System.Runtime.CompilerServices.Unsafe.SizeOf<SkinPaletteMatrix>();
        nuint byteCount = checked((nuint)count * stride);
        var copy = new PhysicsChainComputeBufferCopy(source,
            checked((nint)((nuint)sourceBase * stride)), destination,
            checked((nint)((nuint)destinationBase * stride)), byteCount);
        if (!TryCopyBuffer(backend, copy, "previous-output-palette"))
            return false;
        RecordGpuCopyBytes(checked((long)byteCount), _currentDispatchGroupIsBatched);
        return true;
    }

    private void AbandonOutputPage(IPhysicsChainComputeBackend backend)
    {
        lock (_outputPageSync)
        {
            if (_writingOutputPageIndex >= 0)
            {
                PhysicsChainOutputPage page = _outputPages[_writingOutputPageIndex];
                page.PreviousPaletteSourceToken = default;
                page.RendererStates.Clear();
                page.MorphWeights.Clear();
                page.SpatialCheckpoints.Clear();
                if (page.HasQueuedGpuWork)
                {
                    try
                    {
                        page.ProducerFence = backend.InsertFence();
                    }
                    catch (Exception exception)
                    {
                        XREngine.Debug.LogException(exception);
                        page.ProducerFence = null;
                    }
                    page.HasUnfencedGpuWork = page.ProducerFence is null;
                    if (page.HasUnfencedGpuWork)
                        ++_outputPageFailureCount;
                }
            }
            _writingOutputPageIndex = -1;
            _pendingBoundsSources.Clear();
            _pendingPaletteInputBoundsByRenderer.Clear();
            _pendingGpuBoundsCommandCount = 0;
            _gpuBoundsCopiedRenderers.Clear();
        }
    }

    private void DisposeOutputPages()
    {
        RetireOutputPages();
        lock (_outputPageSync)
        {
            for (int index = _retiredOutputPages.Count - 1; index >= 0; --index)
            {
                PhysicsChainOutputPage page = _retiredOutputPages[index].Page;
                if (page.RetainCount == 0 && (page.NativeResourcesRetired || CanDisposeOutputPage(page)))
                {
                    DisposeOutputPageResources(page);
                    _retiredOutputPages.RemoveAt(index);
                }
            }
        }
    }

    private static bool CanDisposeOutputPage(PhysicsChainOutputPage page)
    {
        if (page.NativeResourcesRetired)
            return true;
        if (page.ProducerRenderer is not null
            && !ReferenceEquals(page.ProducerRenderer, AbstractRenderer.Current))
            return false;
        bool uncertainProducer = page.HasUnfencedGpuWork || page.KnownProducerFailure;
        if (page.ProducerFence is not null)
        {
            EGpuFenceSubmissionStatus status = page.ProducerFence.SubmissionStatus;
            if (status == EGpuFenceSubmissionStatus.AwaitingSubmission)
                return false;
            if (status == EGpuFenceSubmissionStatus.Failed)
                uncertainProducer = true;
            else if (page.ProducerFence.Poll() != EGpuFenceStatus.Signaled)
                return false;
        }

        if (AbstractRenderer.Current is IRuntimeRendererHost host
            && host.BackendId == RendererBackendId.Vulkan)
        {
            if (uncertainProducer && (host.IsDeviceLost || !AbstractRenderer.Current.AcceptsBackendWork))
                return false;
            if (!host.TryGetBackendCapability<IGpuBufferContentReuseCapability>(out var capability)
                || capability is null)
                return false;
            if (!IsNativeReuseReady(capability, page.BoundsAtlas)
                || !IsNativeReuseReady(capability, page.SlotMetadata)
                || !IsNativeReuseReady(capability, page.CurrentPalette)
                || !IsNativeReuseReady(capability, page.PreviousPalette))
                return false;
        }
        else if (uncertainProducer)
            return false;

        return true;
    }

    private void DisposeUnpagedPaletteBuffers()
    {
        _unpagedSkinPaletteBuffer?.Dispose();
        if (!ReferenceEquals(_unpagedPreviousSkinPaletteBuffer, _unpagedSkinPaletteBuffer))
            _unpagedPreviousSkinPaletteBuffer?.Dispose();
        _unpagedSkinPaletteBuffer = null;
        _unpagedPreviousSkinPaletteBuffer = null;
    }

    private static void DisposeOutputPageResources(PhysicsChainOutputPage page)
    {
        page.BoundsAtlas?.Dispose();
        page.SlotMetadata?.Dispose();
        page.CurrentPalette?.Dispose();
        page.PreviousPalette?.Dispose();
        page.ProducerFence?.Dispose();
        page.BoundsAtlas = null;
        page.SlotMetadata = null;
        page.CurrentPalette = null;
        page.PreviousPalette = null;
        page.ProducerFence = null;
        page.Published = false;
        page.PaletteSlices.Clear();
        page.PaletteHistoryResetRenderers.Clear();
        page.RendererStates.Clear();
        page.MorphWeights.Clear();
        page.SpatialCheckpoints.Clear();
        page.PreviousPaletteSourceToken = default;
    }

    private void SweepRetiredOutputPages()
    {
        for (int index = _retiredOutputPages.Count - 1; index >= 0; --index)
        {
            PhysicsChainOutputPage page = _retiredOutputPages[index].Page;
            if (page.RetainCount != 0 || !CanDisposeOutputPage(page))
                continue;
            DisposeOutputPageResources(page);
            _retiredOutputPages.RemoveAt(index);
        }
    }

    private static bool RetireOutputPageNativeResources(PhysicsChainOutputPage page)
    {
        try
        {
            page.BoundsAtlas?.Destroy(now: true);
            page.SlotMetadata?.Destroy(now: true);
            page.CurrentPalette?.Destroy(now: true);
            page.PreviousPalette?.Destroy(now: true);
            page.ProducerFence?.Dispose();
            page.ProducerFence = null;
            page.HasUnfencedGpuWork = false;
            page.PreviousPaletteSourceToken = default;
            page.RendererStates.Clear();
            page.MorphWeights.Clear();
            page.SpatialCheckpoints.Clear();
            return (page.BoundsAtlas?.IsDestroyed ?? true)
                && (page.SlotMetadata?.IsDestroyed ?? true)
                && (page.CurrentPalette?.IsDestroyed ?? true)
                && (page.PreviousPalette?.IsDestroyed ?? true);
        }
        catch (Exception exception)
        {
            XREngine.Debug.LogException(exception);
            return false;
        }
    }

    private sealed class PhysicsChainOutputPage
    {
        public XRDataBuffer<uint>? BoundsAtlas;
        public XRDataBuffer<uint>? SlotMetadata;
        public XRDataBuffer<SkinPaletteMatrix>? CurrentPalette;
        public XRDataBuffer<SkinPaletteMatrix>? PreviousPalette;
        public XRGpuFence? ProducerFence;
        public AbstractRenderer? ProducerRenderer;
        public int RetainCount;
        public uint Generation;
        public uint ProducerEpoch;
        public long RenderFrame;
        public bool Published;
        public bool HasUnfencedGpuWork;
        public bool HasQueuedGpuWork;
        public bool KnownProducerFailure;
        public bool FailureRecoveryRequested;
        public int PaletteSignature = int.MinValue;
        public PhysicsChainGpuOutputPageToken PreviousPaletteSourceToken;
        public readonly Dictionary<XRMeshRenderer, PhysicsChainGpuRendererOutputState> RendererStates =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        public readonly List<PhysicsChainMorphWeight> MorphWeights = [];
        public readonly Dictionary<GPUPhysicsChainRequest, PhysicsChainResidentSpatialCheckpoint> SpatialCheckpoints = [];
        public readonly Dictionary<XRMeshRenderer, (uint Base, uint Count, long BoneGeneration)> PaletteSlices =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        public readonly HashSet<XRMeshRenderer> PaletteHistoryResetRenderers =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        public bool DisposeRequested;
        public bool KnownIdle;
        public bool NativeResourcesRetired;
    }

    private readonly record struct RetiredOutputPage(int Index, PhysicsChainOutputPage Page);
}
