using System;
using System.Collections.Concurrent;
using System.Threading;
using Silk.NET.OpenGL;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class GLTexture2D
{
    private readonly ConcurrentDictionary<nint, int> _deferredSparseTransitionStorageGenerations = new();
    private int _sparseAsyncWorkCount;
    private Action<SparseTextureStreamingTransitionResult>? _discardSparseTransition;

    private Action<SparseTextureStreamingTransitionResult> DiscardSparseTransition
        => _discardSparseTransition ??= ScheduleDiscardSparseTextureStreamingTransition;

    protected override bool HasInFlightNativeOperation
        => Volatile.Read(ref _sparseAsyncWorkCount) != 0;

    internal bool HasPendingSparseAsyncWork
        => HasInFlightNativeOperation;

    private void ReleaseSparseAsyncWork()
    {
        if (Interlocked.Decrement(ref _sparseAsyncWorkCount) < 0)
        {
            Interlocked.Exchange(ref _sparseAsyncWorkCount, 0);
            throw new InvalidOperationException("A sparse texture async operation was released more than once.");
        }
    }

    private readonly record struct PreparedSparseTransition(
        SparseTextureStreamingTransitionRequest Request,
        SparseTextureStreamingSupport Support,
        SparseTextureStreamingPageSelection DesiredPageSelection,
        int RequestedBaseMipLevel,
        int CommittedBaseMipLevel,
        int PreviousCommittedBaseMipLevel,
        int NumSparseLevels,
        long CommittedBytes,
        int StorageGeneration);

    internal bool TryScheduleSparseTextureStreamingTransitionAsync(
        SparseTextureStreamingTransitionRequest request,
        CancellationToken cancellationToken,
        Action<SparseTextureStreamingTransitionResult> onCompleted,
        Action<Exception>? onError = null)
    {
        // The async promotion path is completed by polling a GL sync object. Keep
        // strict zero-readback profiling on the single-context fallback path.
        if (RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy().IsGpuZeroReadbackStrategy())
            return false;

        PrepareForBindlessHandle();
        if (HasInFlightNativeOperation || !EnsureBindlessParametersMutable())
            return false;

        if (!TryPrepareSparseTransitionForAsyncPromotion(request, out PreparedSparseTransition prepared))
            return false;

        if (cancellationToken.IsCancellationRequested)
            return false;

        Interlocked.Increment(ref _sparseAsyncWorkCount);
        uint textureBindingId = BindingId;
        GLEnum textureTarget = ToGLEnum(TextureTarget);
        try
        {
            if (!Renderer.TryEnqueueSharedContextJob(gl =>
                ExecuteSparsePromotionOnSharedContext(
                    gl,
                    textureTarget,
                    textureBindingId,
                    prepared,
                    cancellationToken,
                    onCompleted,
                    onError)))
            {
                ReleaseSparseAsyncWork();
                return false;
            }
        }
        catch
        {
            ReleaseSparseAsyncWork();
            throw;
        }

        return true;
    }

    internal SparseTextureStreamingFinalizeResult FinalizeSparseTextureStreamingTransition(
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingTransitionResult transitionResult)
    {
        if (!transitionResult.ExposureDeferred)
            return SparseTextureStreamingFinalizeResult.Success();

        if (!RuntimeEngine.IsRenderThread)
            return SparseTextureStreamingFinalizeResult.Failed("Sparse texture transition finalization must run on the render thread.");

        if (transitionResult.FenceSync == 0)
            return SparseTextureStreamingFinalizeResult.Failed("Sparse texture transition is missing a fence sync object.");

        Generate();

        IGLTexture? previousTexture = Renderer.BoundTexture;
        bool restorePrevious = previousTexture is not null && !ReferenceEquals(previousTexture, this);
        Api.BindTexture(ToGLEnum(TextureTarget), BindingId);
        Renderer.SetBoundTexture(TextureTarget, this, Data.Name);

        try
        {
            GLEnum waitResult = Api.ClientWaitSync(transitionResult.FenceSync, 0u, 0u);
            if (waitResult != GLEnum.AlreadySignaled && waitResult != GLEnum.ConditionSatisfied)
            {
                if (waitResult == GLEnum.WaitFailed)
                {
                    // A failed wait cannot prove that the shared-context upload has
                    // stopped using this identity. Keep the wrapper quarantined.
                    return SparseTextureStreamingFinalizeResult.Failed("glClientWaitSync failed while finalizing a sparse texture promotion.");
                }

                return SparseTextureStreamingFinalizeResult.Pending();
            }

            if (!_deferredSparseTransitionStorageGenerations.TryRemove(transitionResult.FenceSync, out int storageGeneration))
                return SparseTextureStreamingFinalizeResult.Failed("Sparse upload fence ownership was already released.");

            if (!IsStorageGenerationCurrent(storageGeneration))
            {
                Api.DeleteSync(transitionResult.FenceSync);
                ReleaseSparseAsyncWork();
                return SparseTextureStreamingFinalizeResult.Failed("Sparse texture storage changed before deferred promotion finalization.");
            }

            Api.DeleteSync(transitionResult.FenceSync);
            ReleaseSparseAsyncWork();
            if (!transitionResult.Applied || !transitionResult.UsedSparseResidency)
                return SparseTextureStreamingFinalizeResult.Failed(transitionResult.FailureReason ?? "Sparse upload failed before publication.");
            SetSparseMipSamplingRange(transitionResult.RequestedBaseMipLevel, Math.Max(0, request.LogicalMipCount - 1));
            UncommitSparseCoverageDifference(
                Renderer.GetSparseTextureStreamingSupport(request.SizedInternalFormat),
                Data.SparseTextureStreamingResidentPageSelection,
                request.PageSelection,
                Data.SparseTextureStreamingCommittedBaseMipLevel,
                transitionResult.CommittedBaseMipLevel,
                transitionResult.NumSparseLevels,
                request.LogicalWidth,
                request.LogicalHeight,
                request.LogicalMipCount);
            UpdateSparseCommittedBytes(transitionResult.CommittedBytes);
            UpdateSparseTextureState(
                request,
                request.PageSelection,
                transitionResult.RequestedBaseMipLevel,
                transitionResult.CommittedBaseMipLevel,
                transitionResult.NumSparseLevels,
                transitionResult.CommittedBytes);
            return SparseTextureStreamingFinalizeResult.Success();
        }
        catch (Exception ex)
        {
            Debug.OpenGLException(ex);
            return SparseTextureStreamingFinalizeResult.Failed(ex.Message);
        }
        finally
        {
            if (restorePrevious)
                previousTexture!.Bind();
            else
            {
                Renderer.SetBoundTexture(TextureTarget, null);
                Api.BindTexture(ToGLEnum(TextureTarget), 0);
            }
        }
    }

    /// <summary>Retires a canceled shared-context upload without exposing its pages.</summary>
    internal bool TryDiscardSparseTextureStreamingTransition(SparseTextureStreamingTransitionResult transitionResult)
    {
        if (transitionResult.FenceSync == 0 ||
            !_deferredSparseTransitionStorageGenerations.ContainsKey(transitionResult.FenceSync))
            return true;

        GLEnum waitResult = Api.ClientWaitSync(transitionResult.FenceSync, 0u, 0u);
        if (waitResult == GLEnum.TimeoutExpired)
            return false;
        if (waitResult == GLEnum.WaitFailed)
        {
            Debug.OpenGLWarning("A canceled sparse upload fence failed; retaining its native texture identity.");
            return true;
        }

        if (_deferredSparseTransitionStorageGenerations.TryRemove(transitionResult.FenceSync, out _))
        {
            Api.DeleteSync(transitionResult.FenceSync);
            ReleaseSparseAsyncWork();
        }
        return true;
    }

    private void ScheduleDiscardSparseTextureStreamingTransition(SparseTextureStreamingTransitionResult transitionResult)
        => RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadCoroutine(
            () => TryDiscardSparseTextureStreamingTransition(transitionResult),
            $"XRTexture2D.DiscardSparseTransition[{Data.Name}]",
            RenderThreadJobKind.TextureUpload);

    private bool TryPrepareSparseTransitionForAsyncPromotion(
        SparseTextureStreamingTransitionRequest request,
        out PreparedSparseTransition prepared)
    {
        prepared = default;

        if (!RuntimeEngine.IsRenderThread)
            return false;

        if (!Renderer.HasSharedContext
            || request.ResidentMipmaps is null
            || request.ResidentMipmaps.Length == 0
            || Data.MultiSample
            || Data.Resizable
            || Data.UsesOpenGlExternalMemoryImport)
        {
            return false;
        }

        Generate();

        IGLTexture? previousTexture = Renderer.BoundTexture;
        bool restorePrevious = previousTexture is not null && !ReferenceEquals(previousTexture, this);
        Api.BindTexture(ToGLEnum(TextureTarget), BindingId);
        Renderer.SetBoundTexture(TextureTarget, this, Data.Name);

        try
        {
            ResetUnpackStateForTextureUpload();

            SparseTextureStreamingSupport support = Renderer.GetSparseTextureStreamingSupport(request.SizedInternalFormat);
            if (!support.IsAvailable || !support.IsPageAligned(request.LogicalWidth, request.LogicalHeight))
                return false;

            int previousCommittedBaseMipLevel = Data.SparseTextureStreamingCommittedBaseMipLevel;
            bool hasPreviousCommit = previousCommittedBaseMipLevel != int.MaxValue;
            if (!hasPreviousCommit)
                return false;

            if (!HasPublishedSparseStorageForAsyncPromotion(request))
                return false;

            int numSparseLevels = _sparseNumSparseLevels;
            int requestedBaseMipLevel = Math.Clamp(request.RequestedBaseMipLevel, 0, Math.Max(0, request.LogicalMipCount - 1));
            int committedBaseMipLevel = XRTexture2D.ResolveSparseCommittedBaseMipLevel(requestedBaseMipLevel, numSparseLevels, request.LogicalMipCount);
            bool isDemotion = hasPreviousCommit && committedBaseMipLevel > previousCommittedBaseMipLevel;
            if (isDemotion)
                return false;

            int currentVisibleBaseMipLevel = Math.Clamp(Data.SparseTextureStreamingResidentBaseMipLevel, 0, Math.Max(0, request.LogicalMipCount - 1));
            if (requestedBaseMipLevel >= currentVisibleBaseMipLevel)
                return false;

            int tailFirstMipLevel = Math.Min(Math.Max(0, numSparseLevels), request.LogicalMipCount);
            SparseTextureStreamingPageSelection desiredPageSelection = request.PageSelection.Normalize();
            if (!desiredPageSelection.IsPartial || committedBaseMipLevel >= tailFirstMipLevel)
                desiredPageSelection = SparseTextureStreamingPageSelection.Full;

            SetSparseMipSamplingRange(currentVisibleBaseMipLevel, request.LogicalMipCount - 1);
            ClearInvalidation();

            long committedBytes = XRTexture2D.EstimateSparsePageSelectionBytes(
                request.LogicalWidth,
                request.LogicalHeight,
                requestedBaseMipLevel,
                request.LogicalMipCount,
                numSparseLevels,
                support,
                desiredPageSelection,
                request.SizedInternalFormat);
            prepared = new PreparedSparseTransition(
                request,
                support,
                desiredPageSelection,
                requestedBaseMipLevel,
                committedBaseMipLevel,
                previousCommittedBaseMipLevel,
                numSparseLevels,
                committedBytes,
                CurrentStorageGeneration);
            return true;
        }
        catch (Exception ex)
        {
            Debug.OpenGLException(ex);
            return false;
        }
        finally
        {
            if (restorePrevious)
                previousTexture!.Bind();
            else
            {
                Renderer.SetBoundTexture(TextureTarget, null);
                Api.BindTexture(ToGLEnum(TextureTarget), 0);
            }
        }
    }

    private bool HasPublishedSparseStorageForAsyncPromotion(SparseTextureStreamingTransitionRequest request)
    {
        return _sparseStorageAllocated
            && StorageSet
            && _sparseLogicalWidth == request.LogicalWidth
            && _sparseLogicalHeight == request.LogicalHeight
            && _sparseLogicalMipCount == request.LogicalMipCount
            && _allocatedLevels >= (uint)Math.Max(1, request.LogicalMipCount)
            && _allocatedInternalFormat == request.SizedInternalFormat
            && Data.SparseTextureStreamingEnabled
            && Data.SparseTextureStreamingLogicalWidth == request.LogicalWidth
            && Data.SparseTextureStreamingLogicalHeight == request.LogicalHeight
            && Data.SparseTextureStreamingLogicalMipCount == request.LogicalMipCount
            && Data.SparseTextureStreamingResidentBaseMipLevel != int.MaxValue
            && Data.SparseTextureStreamingCommittedBaseMipLevel != int.MaxValue
            && Data.SparseTextureStreamingCommittedBytes > 0L;
    }

    private void ExecuteSparsePromotionOnSharedContext(
        GL gl,
        GLEnum textureTarget,
        uint textureBindingId,
        PreparedSparseTransition prepared,
        CancellationToken cancellationToken,
        Action<SparseTextureStreamingTransitionResult> onCompleted,
        Action<Exception>? onError)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            ReleaseSparseAsyncWork();
            onCompleted(SparseTextureStreamingTransitionResult.Unsupported("Sparse texture promotion was canceled before GPU submission."));
            return;
        }

        bool gpuWorkStarted = false;
        bool ownerReleased = false;
        SparseTextureStreamingTransitionResult? transferredResult = null;
        try
        {
            if (!IsStorageGenerationCurrent(prepared.StorageGeneration))
            {
                ReleaseSparseAsyncWork();
                ownerReleased = true;
                onCompleted(SparseTextureStreamingTransitionResult.Unsupported("Sparse texture storage changed before async promotion began."));
                return;
            }

            gl.BindTexture(textureTarget, textureBindingId);
            ResetUnpackStateForTextureUpload(gl);

            gpuWorkStarted = true;
            CommitDesiredSparseCoverage(
                prepared.Support,
                prepared.DesiredPageSelection,
                prepared.CommittedBaseMipLevel,
                prepared.NumSparseLevels,
                prepared.Request.LogicalWidth,
                prepared.Request.LogicalHeight,
                prepared.Request.LogicalMipCount);
            UploadSparseResidentMipmaps(gl, prepared.Request, prepared.Support, prepared.DesiredPageSelection, prepared.NumSparseLevels);

            nint fenceSync = gl.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
            gl.Flush();

            if (fenceSync == 0)
            {
                onError?.Invoke(new InvalidOperationException("glFenceSync returned an invalid handle after sparse texture upload; native identity is retained."));
                return;
            }

            _deferredSparseTransitionStorageGenerations[fenceSync] = prepared.StorageGeneration;
            bool storageCurrent = IsStorageGenerationCurrent(prepared.StorageGeneration);
            SparseTextureStreamingTransitionResult result = new(
                Applied: storageCurrent,
                UsedSparseResidency: true,
                RequestedBaseMipLevel: prepared.RequestedBaseMipLevel,
                CommittedBaseMipLevel: prepared.CommittedBaseMipLevel,
                NumSparseLevels: prepared.NumSparseLevels,
                CommittedBytes: prepared.CommittedBytes,
                ExposureDeferred: true,
                FenceSync: fenceSync,
                FailureReason: storageCurrent ? null : "Sparse texture storage changed before async promotion completed.",
                DiscardDeferred: DiscardSparseTransition);
            transferredResult = result;
            onCompleted(result);
        }
        catch (Exception ex)
        {
            if (transferredResult is { } result)
            {
                ScheduleDiscardSparseTextureStreamingTransition(result);
                onError?.Invoke(ex);
                return;
            }

            if (!gpuWorkStarted)
            {
                if (!ownerReleased)
                    ReleaseSparseAsyncWork();
                onError?.Invoke(ex);
            }
            else
            {
                nint fenceSync = 0;
                try
                {
                    fenceSync = gl.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
                    gl.Flush();
                }
                catch (Exception fenceError)
                {
                    onError?.Invoke(new InvalidOperationException("Sparse upload failed and could not be fenced; native identity is retained.", new AggregateException(ex, fenceError)));
                    return;
                }
                if (fenceSync == 0)
                {
                    onError?.Invoke(new InvalidOperationException("Sparse upload failed and could not be fenced; native identity is retained.", ex));
                }
                else
                {
                    _deferredSparseTransitionStorageGenerations[fenceSync] = prepared.StorageGeneration;
                    SparseTextureStreamingTransitionResult failure = new(
                        Applied: false,
                        UsedSparseResidency: true,
                        RequestedBaseMipLevel: prepared.RequestedBaseMipLevel,
                        CommittedBaseMipLevel: prepared.CommittedBaseMipLevel,
                        NumSparseLevels: prepared.NumSparseLevels,
                        CommittedBytes: prepared.CommittedBytes,
                        ExposureDeferred: true,
                        FenceSync: fenceSync,
                        FailureReason: ex.Message,
                        DiscardDeferred: DiscardSparseTransition);
                    try { onCompleted(failure); }
                    catch (Exception callbackError)
                    {
                        ScheduleDiscardSparseTextureStreamingTransition(failure);
                        onError?.Invoke(new AggregateException(ex, callbackError));
                    }
                }
            }
        }
        finally
        {
            gl.BindTexture(textureTarget, 0);
        }
    }

    private static unsafe void UploadSparseResidentMipmaps(
        GL gl,
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingSupport support,
        SparseTextureStreamingPageSelection selection,
        int numSparseLevels)
    {
        GLEnum target = ToGLEnum(ETextureTarget.Texture2D);
        ResetUnpackStateForTextureUpload(gl);
        Mipmap2D[] residentMipmaps = request.ResidentMipmaps;
        bool usePartialPages = selection.IsPartial;
        int tailFirstMipLevel = Math.Min(Math.Max(0, numSparseLevels), request.LogicalMipCount);
        for (int i = 0; i < residentMipmaps.Length; i++)
        {
            Mipmap2D mip = residentMipmaps[i];
            DataSource? mipData = mip.Data;
            if (mipData is null || mipData.Length == 0)
                continue;

            uint sourceBytesPerPixel = GetSourceBytesPerPixel(mip);

            int mipLevel = request.RequestedBaseMipLevel + i;
            if (usePartialPages
                && mipLevel < tailFirstMipLevel
                && XRTexture2D.TryResolveSparsePageRegion(support, selection, request.LogicalWidth, request.LogicalHeight, mipLevel, out SparseTextureStreamingPageRegion region)
                && region.HasArea)
            {
                using DataSource regionData = CreateSparseMipRegionData(mip, region, sourceBytesPerPixel);
                gl.TexSubImage2D(
                    target,
                    mipLevel,
                    region.XOffset,
                    region.YOffset,
                    region.Width,
                    region.Height,
                    ToGLEnum(mip.PixelFormat),
                    ToGLEnum(mip.PixelType),
                    regionData.Address.Pointer);
                continue;
            }

            gl.TexSubImage2D(
                target,
                mipLevel,
                0,
                0,
                mip.Width,
                mip.Height,
                ToGLEnum(mip.PixelFormat),
                ToGLEnum(mip.PixelType),
                mipData.Address.Pointer);
        }
    }
}
