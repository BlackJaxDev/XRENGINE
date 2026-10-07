using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private const int InputPageCount = 8;
    private PhysicsChainInputPage[] _inputPages = CreateInputPages();
    private readonly List<PhysicsChainInputPage> _retiredInputPages = [];
    private readonly List<PhysicsChainInputPage> _inputPagesUsedThisFrame = new(InputPageCount);
    private readonly List<PhysicsChainAffineInput> _transformInputCatalog = [];
    private readonly List<PhysicsChainAffineInput> _transformCatalogRollbackValues = [];
    private readonly List<(int Offset, int Count, int SavedOffset)> _transformCatalogRollbackRanges = [];
    private PhysicsChainInputPage? _currentInputPage;
    private ulong _inputPageSubmissionOrdinal;
    private bool _inputPublicationBarrierPending;
    private long _inputPageBusyCount;
    private long _inputPageFailureCount;
    private long _inputPageAcquiredCount;
    private long _inputPageBufferAllocationCount;
    private long _inputPageMappedWriteCount;
    private string _lastInputPageFailure = string.Empty;

    /// <summary>Reports input pages that cannot yet accept new CPU writes.</summary>
    public PhysicsChainInputPageDiagnostics InputPageDiagnostics
    {
        get
        {
            int quarantined = 0;
            for (int index = 0; index < _inputPages.Length; ++index)
                if (_inputPages[index].Quarantined)
                    ++quarantined;
            return new(_inputPageBusyCount, _inputPageFailureCount, _lastInputPageFailure,
                _inputPageAcquiredCount, _inputPageBufferAllocationCount,
                _inputPageMappedWriteCount, quarantined);
        }
    }

    private static PhysicsChainInputPage[] CreateInputPages()
        => [new(), new(), new(), new(), new(), new(), new(), new()];

    private bool TryAcquireInputPage(IPhysicsChainComputeBackend backend)
    {
        SweepRetiredInputPages(backend);
        for (int index = 0; index < InputPageCount; index++)
        {
            PhysicsChainInputPage page = _inputPages[index];
            if (page.Reserved || !CanReuseInputPage(backend, page))
                continue;

            page.Reserved = true;
            page.HasWritten = false;
            page.HasQueuedWork = false;
            page.FailedMarkerRecorded = false;
            page.ProducerRenderer = backend.Renderer;
            _currentInputPage = page;
            _inputPagesUsedThisFrame.Add(page);
            _perTreeParamsBuffer = page.Headers;
            _instanceMetadataBuffer = page.Instances;
            _treeWorkItemBuffer = page.TreeWork;
            _collidersBuffer = page.Colliders;
            _transformMatricesBuffer = page.Transforms;
            _mainPassBindingsValid = false;
            _dynamicHeaderResourceGeneration = -1;
            _activeWorkResourceGeneration = -1;
            ++_inputPageAcquiredCount;
            return true;
        }

        ++_inputPageBusyCount;
        _lastInputPageFailure = "InputPageBusyOrUnsafe";
        return false;
    }

    private bool CanReuseInputPage(IPhysicsChainComputeBackend backend, PhysicsChainInputPage page)
    {
        if (page.Quarantined || page.ProducerRenderer is not null &&
            !ReferenceEquals(page.ProducerRenderer, backend.Renderer))
            return false;

        bool failedMarker = false;
        if (page.Fence is { } fence)
        {
            if (fence.IsDisposed)
            {
                page.Quarantined = true;
                RecordInputMarkerFailureOnce(page, "InputPageFenceDisposed");
                return false;
            }
            EGpuFenceSubmissionStatus submission = fence.SubmissionStatus;
            if (submission == EGpuFenceSubmissionStatus.AwaitingSubmission)
                return false;
            failedMarker = submission == EGpuFenceSubmissionStatus.Failed;
            if (!failedMarker)
            {
                EGpuFenceStatus status = fence.Poll();
                if (status == EGpuFenceStatus.Pending)
                    return false;
                failedMarker = status == EGpuFenceStatus.Failed;
            }
            if (failedMarker)
                RecordInputMarkerFailureOnce(page, "InputPageFenceFailed");
        }
        else if (page.HasWritten || page.HasQueuedWork)
        {
            page.Quarantined = true;
            RecordInputMarkerFailureOnce(page, "InputPageFenceUnavailable");
            return false;
        }

        if (backend.Renderer is IRuntimeRendererHost host &&
            host.BackendId == RendererBackendId.Vulkan)
        {
            if (host.IsDeviceLost || !backend.Renderer.AcceptsBackendWork)
            {
                if (failedMarker)
                    page.Quarantined = true;
                return false;
            }
            if (!host.TryGetBackendCapability<IGpuBufferContentReuseCapability>(out var reuse) ||
                reuse is null)
            {
                if (failedMarker)
                    page.Quarantined = true;
                return false;
            }
            if (!IsInputBufferReady(reuse, page.Headers) ||
                !IsInputBufferReady(reuse, page.Instances) ||
                !IsInputBufferReady(reuse, page.TreeWork) ||
                !IsInputBufferReady(reuse, page.Colliders) ||
                !IsInputBufferReady(reuse, page.Transforms))
                return false;
        }
        else if (failedMarker)
        {
            page.Quarantined = true;
            return false;
        }

        page.Fence?.Dispose();
        page.Fence = null;
        page.HasWritten = false;
        page.HasQueuedWork = false;
        page.FailedMarkerRecorded = false;
        return true;
    }

    private void RecordInputMarkerFailureOnce(PhysicsChainInputPage page, string stage)
    {
        if (page.FailedMarkerRecorded)
            return;
        page.FailedMarkerRecorded = true;
        RecordInputPageFailure(stage);
    }

    private static bool IsInputBufferReady(IGpuBufferContentReuseCapability reuse, XRDataBuffer? buffer)
    {
        if (buffer is null)
            return true;
        XRBufferStateSnapshot state = buffer.GetStateSnapshot();
        return !state.IsApiObjectGenerated && state.UploadedByteCount == 0u ||
            reuse.QueryBufferContentReuse(buffer) == EGpuBufferContentReuseStatus.Ready;
    }

    private bool EnsureInputPageCapacity(IPhysicsChainComputeBackend backend,
        int headerCount, int instanceCount, int treeWorkCount, int colliderCount,
        int transformCount)
    {
        PhysicsChainInputPage? page = _currentInputPage;
        if (page is null)
            return RecordInputPageFailure("InputPageNotReserved");

        if (!TryEnsureMappedInputBuffer(backend, ref page.Headers, "PhysicsChainDynamicHeaderPage",
                headerCount) ||
            !TryEnsureMappedInputBuffer(backend, ref page.Instances, "PhysicsChainInstancePage",
                instanceCount) ||
            !TryEnsureMappedInputBuffer(backend, ref page.TreeWork, "PhysicsChainTreeWorkPage",
                treeWorkCount) ||
            !TryEnsureMappedInputBuffer(backend, ref page.Colliders, "PhysicsChainColliderPage",
                colliderCount) ||
            !TryEnsureMappedInputBuffer(backend, ref page.Transforms, "PhysicsChainTransformPage",
                transformCount))
            return RecordInputPageFailure("InputPageMapOrCapacity");

        _perTreeParamsBuffer = page.Headers;
        _instanceMetadataBuffer = page.Instances;
        _treeWorkItemBuffer = page.TreeWork;
        _collidersBuffer = page.Colliders;
        _transformMatricesBuffer = page.Transforms;
        return true;
    }

    private bool TryEnsureMappedInputBuffer<T>(IPhysicsChainComputeBackend backend,
        ref XRDataBuffer<T>? buffer, string name, int requiredCount) where T : unmanaged
    {
        if (requiredCount < 0 || requiredCount > MaxArenaElementCount)
            return false;
        uint required = (uint)Math.Max(requiredCount, 1);
        if (buffer is not null && buffer.ElementCount >= required)
            return buffer.IsMapped || TryMapInputBuffer(backend, buffer);

        uint capacity = XRMath.NextPowerOfTwo(required);
        if (capacity > MaxArenaElementCount ||
            (ulong)capacity * (uint)Unsafe.SizeOf<T>() > int.MaxValue)
            return false;

        var successor = new XRDataBuffer<T>(name, EBufferTarget.ShaderStorageBuffer, capacity)
        {
            DisposeOnPush = false,
            Usage = EBufferUsage.StreamDraw,
            ShouldMap = true,
            StorageFlags = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Write |
                EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent,
            RangeFlags = EBufferMapRangeFlags.Write | EBufferMapRangeFlags.Persistent |
                EBufferMapRangeFlags.Coherent,
        };
        if (!TryMapInputBuffer(backend, successor))
        {
            successor.Dispose();
            return false;
        }
        buffer?.Dispose();
        buffer = successor;
        ++_inputPageBufferAllocationCount;
        return true;
    }

    private static bool TryMapInputBuffer(IPhysicsChainComputeBackend backend, XRDataBuffer buffer)
    {
        if (!backend.EnsureGpuBufferReady(buffer))
            return false;
        if (!buffer.IsMapped)
            buffer.MapBufferData();
        return buffer.IsMapped;
    }

    private ref struct InputMappedWrite<T> where T : unmanaged
    {
        internal ReadOnlySpan<T> Source;
        internal int ByteOffset;
    }

    private bool TryWriteInputPage<T>(XRDataBuffer<T>? buffer,
        ReadOnlySpan<T> source, int elementOffset) where T : unmanaged
    {
        if (buffer is null || elementOffset < 0 ||
            (ulong)elementOffset + (uint)source.Length > buffer.ElementCount)
            return RecordInputPageFailure("InputPageWriteRange");
        int byteOffset = checked(elementOffset * Unsafe.SizeOf<T>());
        var state = new InputMappedWrite<T> { Source = source, ByteOffset = byteOffset };
        _currentInputPage!.HasWritten = true;
        if (!buffer.TryWriteMapped(ref state,
                static (scoped Span<byte> destination, ref InputMappedWrite<T> input) =>
                {
                    ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(input.Source);
                    if (input.ByteOffset > destination.Length ||
                        bytes.Length > destination.Length - input.ByteOffset)
                        return false;
                    bytes.CopyTo(destination.Slice(input.ByteOffset, bytes.Length));
                    return true;
                }))
            return RecordInputPageFailure("InputPageMappedWrite");
        RecordArenaUpload(checked((uint)(source.Length * Unsafe.SizeOf<T>())), isStatic: false);
        ++_inputPageMappedWriteCount;
        return true;
    }

    private bool UploadInputPageColliders(IReadOnlyList<GPUPhysicsChainRequest> requests)
    {
        for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
        {
            GPUPhysicsChainRequest request = requests[requestIndex];
            if (request.Colliders.Count == 0)
                continue;
            if (!TryWriteInputPage(_collidersBuffer,
                    request.AcceptedInput.Colliders.AsSpan(), request.ColliderOffset))
                return false;
            request.UploadedColliderDataVersion = request.ColliderDataSignature;
        }
        return true;
    }

    private void EnsureTransformInputCatalogCapacity()
    {
        _transformInputCatalog.EnsureCapacity(_particleArenaHighWater);
        while (_transformInputCatalog.Count < _particleArenaHighWater)
            _transformInputCatalog.Add(PhysicsChainAffineInput.FromMatrix(Matrix4x4.Identity));
    }

    private void BeginTransformCatalogGroup()
    {
        _transformCatalogRollbackValues.Clear();
        _transformCatalogRollbackRanges.Clear();
    }

    private void RecordTransformCatalogRange(int offset, int count)
    {
        int savedOffset = _transformCatalogRollbackValues.Count;
        _transformCatalogRollbackValues.EnsureCapacity(savedOffset + count);
        Span<PhysicsChainAffineInput> catalog = CollectionsMarshal.AsSpan(_transformInputCatalog);
        for (int index = 0; index < count; ++index)
            _transformCatalogRollbackValues.Add(catalog[offset + index]);
        _transformCatalogRollbackRanges.Add((offset, count, savedOffset));
    }

    private void CommitTransformCatalogGroup() => BeginTransformCatalogGroup();

    private void RollbackTransformCatalogGroup()
    {
        Span<PhysicsChainAffineInput> catalog = CollectionsMarshal.AsSpan(_transformInputCatalog);
        Span<PhysicsChainAffineInput> saved = CollectionsMarshal.AsSpan(_transformCatalogRollbackValues);
        for (int rangeIndex = _transformCatalogRollbackRanges.Count - 1; rangeIndex >= 0; --rangeIndex)
        {
            (int offset, int count, int savedOffset) = _transformCatalogRollbackRanges[rangeIndex];
            saved.Slice(savedOffset, count).CopyTo(catalog.Slice(offset, count));
        }
        BeginTransformCatalogGroup();
    }

    private bool UploadInputPageTransforms()
        => TryWriteInputPage(_transformMatricesBuffer,
            CollectionsMarshal.AsSpan(_transformInputCatalog), 0);

    private bool PublishWithoutSimulationInputPage(IPhysicsChainComputeBackend backend)
    {
        if (_particleArenaHighWater == 0)
            return PublishRegisteredOutputs(backend);
        if (_transformInputCatalog.Count < _particleArenaHighWater)
            return RecordDispatchFailure("InputPageTransformCatalogUnavailable");
        if (!TryAcquireInputPage(backend))
            return RecordDispatchFailure("InputPageBusyOrUnsafe");
        try
        {
            PhysicsChainInputPage page = _currentInputPage!;
            if (!TryEnsureMappedInputBuffer(backend, ref page.Transforms,
                    "PhysicsChainTransformPage", _particleArenaHighWater))
                return RecordDispatchFailure("InputPageTransformMapOrCapacity");
            _transformMatricesBuffer = page.Transforms;
            if (!UploadInputPageTransforms())
                return RecordDispatchFailure("InputPageTransformPublication");
            _inputPublicationBarrierPending = true;
            page.HasQueuedWork = true;
            bool published = PublishRegisteredOutputs(backend);
            return PublishSelectiveReadbacksAfterSimulation(backend) && published;
        }
        finally
        {
            _inputPublicationBarrierPending = false;
            FinishInputGroup();
            SealInputPages(backend);
        }
    }

    private XRDataBuffer<PhysicsChainAffineInput>? GetLastCompletedTransformInput(
        IPhysicsChainComputeBackend backend)
    {
        PhysicsChainInputPage? latest = null;
        for (int index = 0; index < _inputPages.Length; index++)
        {
            PhysicsChainInputPage page = _inputPages[index];
            if (!ReferenceEquals(page.ProducerRenderer, backend.Renderer) ||
                page.Transforms is null || page.Fence is not { } fence ||
                fence.SubmissionStatus != EGpuFenceSubmissionStatus.Submitted ||
                fence.Poll() != EGpuFenceStatus.Signaled ||
                latest is not null && page.SubmissionOrdinal <= latest.SubmissionOrdinal)
                continue;
            latest = page;
        }
        return latest?.Transforms;
    }

    private void FinishInputGroup()
    {
        _currentInputPage = null;
    }

    private void SealInputPages(IPhysicsChainComputeBackend backend)
    {
        for (int index = 0; index < _inputPagesUsedThisFrame.Count; index++)
        {
            PhysicsChainInputPage page = _inputPagesUsedThisFrame[index];
            try
            {
                if (page.HasWritten || page.HasQueuedWork)
                    page.Fence = backend.InsertFence();
                if ((page.HasWritten || page.HasQueuedWork) && page.Fence is null)
                {
                    page.Quarantined = true;
                    RecordInputPageFailure("InputPageFenceUnavailable");
                }
                else if (page.Fence is not null)
                {
                    if (_inputPageSubmissionOrdinal == ulong.MaxValue)
                    {
                        page.Quarantined = true;
                        RecordInputPageFailure("InputPageOrdinalExhausted");
                    }
                    else
                        page.SubmissionOrdinal = ++_inputPageSubmissionOrdinal;
                }
            }
            catch (Exception exception)
            {
                page.Quarantined = true;
                RecordInputPageFailure("InputPageFenceFailed");
                XREngine.Debug.LogException(exception);
            }
            finally
            {
                page.Reserved = false;
            }
        }
        _inputPagesUsedThisFrame.Clear();
        _currentInputPage = null;
    }

    private bool RecordInputPageFailure(string stage)
    {
        ++_inputPageFailureCount;
        _lastInputPageFailure = stage;
        return false;
    }

    private void RetireInputPages()
    {
        for (int index = 0; index < _inputPages.Length; index++)
        {
            PhysicsChainInputPage page = _inputPages[index];
            if (page.ProducerRenderer is not null || page.Headers is not null ||
                page.Instances is not null || page.TreeWork is not null ||
                page.Colliders is not null || page.Transforms is not null)
                _retiredInputPages.Add(page);
        }
        _inputPages = CreateInputPages();
        _currentInputPage = null;
        _perTreeParamsBuffer = null;
        _instanceMetadataBuffer = null;
        _treeWorkItemBuffer = null;
        _collidersBuffer = null;
        _transformMatricesBuffer = null;
        _mainPassBindingsValid = false;
    }

    private void SweepRetiredInputPages(IPhysicsChainComputeBackend backend)
    {
        for (int index = _retiredInputPages.Count - 1; index >= 0; --index)
        {
            PhysicsChainInputPage page = _retiredInputPages[index];
            if (page.Reserved || !CanReuseInputPage(backend, page))
                continue;
            DisposeInputPage(page);
            _retiredInputPages.RemoveAt(index);
        }
    }

    private void DisposeInputPages()
    {
        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is not null &&
            PhysicsChainComputeBackendFactory.TryCreate(renderer,
                out IPhysicsChainComputeBackend? backend) && backend is not null)
            SweepRetiredInputPages(backend);
    }

    private void NotifyInputProducerKnownIdle(AbstractRenderer renderer)
    {
        for (int index = _retiredInputPages.Count - 1; index >= 0; --index)
        {
            PhysicsChainInputPage page = _retiredInputPages[index];
            if (!ReferenceEquals(page.ProducerRenderer, renderer) || page.Reserved)
                continue;
            try
            {
                page.Headers?.Destroy(now: true);
                page.Instances?.Destroy(now: true);
                page.TreeWork?.Destroy(now: true);
                page.Colliders?.Destroy(now: true);
                page.Transforms?.Destroy(now: true);
                page.Fence?.Dispose();
                page.Fence = null;
                page.NativeResourcesRetired = (page.Headers?.IsDestroyed ?? true) &&
                    (page.Instances?.IsDestroyed ?? true) &&
                    (page.TreeWork?.IsDestroyed ?? true) &&
                    (page.Colliders?.IsDestroyed ?? true) &&
                    (page.Transforms?.IsDestroyed ?? true);
                if (!page.NativeResourcesRetired)
                    continue;
                DisposeInputPage(page);
                _retiredInputPages.RemoveAt(index);
            }
            catch (Exception exception)
            {
                RecordInputPageFailure("InputPageNativeRetirement");
                XREngine.Debug.LogException(exception);
            }
        }
    }

    private static void DisposeInputPage(PhysicsChainInputPage page)
    {
        page.Fence?.Dispose();
        page.Fence = null;
        page.Headers?.Dispose();
        page.Instances?.Dispose();
        page.TreeWork?.Dispose();
        page.Colliders?.Dispose();
        page.Transforms?.Dispose();
        page.Headers = null;
        page.Instances = null;
        page.TreeWork = null;
        page.Colliders = null;
        page.Transforms = null;
    }
}
