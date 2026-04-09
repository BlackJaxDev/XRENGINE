using System;
using System.Threading;
using Silk.NET.OpenGL;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class GLTexture2D
{
    private readonly record struct PreparedSparseTransition(
        SparseTextureStreamingTransitionRequest Request,
        SparseTextureStreamingSupport Support,
        SparseTextureStreamingPageSelection DesiredPageSelection,
        int RequestedBaseMipLevel,
        int CommittedBaseMipLevel,
        int PreviousCommittedBaseMipLevel,
        int NumSparseLevels,
        long CommittedBytes);

    internal bool TryScheduleSparseTextureStreamingTransitionAsync(
        SparseTextureStreamingTransitionRequest request,
        CancellationToken cancellationToken,
        Action<SparseTextureStreamingTransitionResult> onCompleted,
        Action<Exception>? onError = null)
    {
        if (!TryPrepareSparseTransitionForAsyncPromotion(request, out PreparedSparseTransition prepared))
            return false;

        if (cancellationToken.IsCancellationRequested)
            return false;

        uint textureBindingId = BindingId;
        GLEnum textureTarget = ToGLEnum(TextureTarget);
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
            return false;
        }

        return true;
    }

    internal SparseTextureStreamingFinalizeResult FinalizeSparseTextureStreamingTransition(
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingTransitionResult transitionResult)
    {
        if (!transitionResult.ExposureDeferred)
            return SparseTextureStreamingFinalizeResult.Success();

        if (!Engine.IsRenderThread)
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
                    Api.DeleteSync(transitionResult.FenceSync);
                    return SparseTextureStreamingFinalizeResult.Failed("glClientWaitSync failed while finalizing a sparse texture promotion.");
                }

                return SparseTextureStreamingFinalizeResult.Pending();
            }

            Api.DeleteSync(transitionResult.FenceSync);
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
            try
            {
                Api.DeleteSync(transitionResult.FenceSync);
            }
            catch
            {
            }

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

    private bool TryPrepareSparseTransitionForAsyncPromotion(
        SparseTextureStreamingTransitionRequest request,
        out PreparedSparseTransition prepared)
    {
        prepared = default;

        if (!Engine.IsRenderThread)
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
            Api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

            SparseTextureStreamingSupport support = Renderer.GetSparseTextureStreamingSupport(request.SizedInternalFormat);
            if (!support.IsAvailable || !support.IsPageAligned(request.LogicalWidth, request.LogicalHeight))
                return false;

            if (!EnsureSparseStorageAllocated(request, support, out int numSparseLevels, out _))
                return false;

            int requestedBaseMipLevel = Math.Clamp(request.RequestedBaseMipLevel, 0, Math.Max(0, request.LogicalMipCount - 1));
            int committedBaseMipLevel = XRTexture2D.ResolveSparseCommittedBaseMipLevel(requestedBaseMipLevel, numSparseLevels, request.LogicalMipCount);
            int previousCommittedBaseMipLevel = Data.SparseTextureStreamingCommittedBaseMipLevel;
            bool hasPreviousCommit = previousCommittedBaseMipLevel != int.MaxValue;
            bool isDemotion = hasPreviousCommit && committedBaseMipLevel > previousCommittedBaseMipLevel;
            if (!hasPreviousCommit || isDemotion)
                return false;

            int currentVisibleBaseMipLevel = Math.Clamp(Data.SparseTextureStreamingResidentBaseMipLevel, 0, Math.Max(0, request.LogicalMipCount - 1));
            if (requestedBaseMipLevel >= currentVisibleBaseMipLevel)
                return false;

            int tailFirstMipLevel = Math.Min(Math.Max(0, numSparseLevels), request.LogicalMipCount);
            SparseTextureStreamingPageSelection desiredPageSelection = request.PageSelection.Normalize();
            if (!desiredPageSelection.IsPartial || committedBaseMipLevel >= tailFirstMipLevel)
                desiredPageSelection = SparseTextureStreamingPageSelection.Full;

            SetSparseMipSamplingRange(currentVisibleBaseMipLevel, request.LogicalMipCount - 1);

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
                committedBytes);
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
            onCompleted(SparseTextureStreamingTransitionResult.Unsupported("Sparse texture promotion was canceled before GPU submission."));
            return;
        }

        try
        {
            gl.BindTexture(textureTarget, textureBindingId);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

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
                onCompleted(SparseTextureStreamingTransitionResult.Unsupported("glFenceSync returned an invalid handle for sparse texture promotion."));
                return;
            }

            onCompleted(new SparseTextureStreamingTransitionResult(
                Applied: true,
                UsedSparseResidency: true,
                RequestedBaseMipLevel: prepared.RequestedBaseMipLevel,
                CommittedBaseMipLevel: prepared.CommittedBaseMipLevel,
                NumSparseLevels: prepared.NumSparseLevels,
                CommittedBytes: prepared.CommittedBytes,
                ExposureDeferred: true,
                FenceSync: fenceSync));
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
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