using Silk.NET.OpenGL;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class GLTexture2D
{
    private const GLEnum TextureSparseArb = (GLEnum)0x91A6;
    private const GLEnum VirtualPageSizeIndexArb = (GLEnum)0x91A7;
    private const GLEnum NumSparseLevelsArb = (GLEnum)0x91AA;

    private bool _sparseStorageAllocated;
    private uint _sparseLogicalWidth;
    private uint _sparseLogicalHeight;
    private int _sparseLogicalMipCount;
    private int _sparseNumSparseLevels;

    private SparseTextureStreamingTransitionResult ApplySparseTextureStreamingTransition(SparseTextureStreamingTransitionRequest request)
    {
        if (!Engine.IsRenderThread)
            return SparseTextureStreamingTransitionResult.Unsupported("Sparse texture transitions must run on the render thread.");

        if (request.ResidentMipmaps is null || request.ResidentMipmaps.Length == 0)
            return SparseTextureStreamingTransitionResult.Unsupported("Sparse texture transition was requested without resident mip data.");

        if (Data.MultiSample || Data.Resizable || Data.UsesOpenGlExternalMemoryImport)
            return SparseTextureStreamingTransitionResult.Unsupported("Sparse texture streaming only supports immutable non-multisampled 2D textures without external memory import.");

        Generate();

        Debug.OpenGL(
            $"[GLTexture2D.Sparse] Applying sparse transition for '{GetDescribingName()}': binding={BindingId} " +
            $"logicalDims={request.LogicalWidth}x{request.LogicalHeight} logicalMips={request.LogicalMipCount} " +
            $"requestedBase={request.RequestedBaseMipLevel} residentMips={request.ResidentMipmaps.Length} " +
            $"sized={request.SizedInternalFormat} pageSelection={request.PageSelection.Normalize()}.");

        IGLTexture? previousTexture = Renderer.BoundTexture;
        bool restorePrevious = previousTexture is not null && !ReferenceEquals(previousTexture, this);
        Api.BindTexture(ToGLEnum(TextureTarget), BindingId);
        Renderer.SetBoundTexture(TextureTarget, this, Data.Name);

        try
        {
            ResetUnpackStateForTextureUpload();

            SparseTextureStreamingSupport support = Renderer.GetSparseTextureStreamingSupport(request.SizedInternalFormat);
            if (!support.IsAvailable)
                return SparseTextureStreamingTransitionResult.Unsupported(support.FailureReason ?? "Sparse texture streaming is unavailable for this format.");

            if (!support.IsPageAligned(request.LogicalWidth, request.LogicalHeight))
            {
                return SparseTextureStreamingTransitionResult.Unsupported(
                    $"Texture dimensions {request.LogicalWidth}x{request.LogicalHeight} are not aligned to sparse page size {support.VirtualPageSizeX}x{support.VirtualPageSizeY}.");
            }

            if (!EnsureSparseStorageAllocated(request, support, out int numSparseLevels, out string? allocationFailure))
                return SparseTextureStreamingTransitionResult.Unsupported(allocationFailure);

            int requestedBaseMipLevel = Math.Clamp(request.RequestedBaseMipLevel, 0, Math.Max(0, request.LogicalMipCount - 1));
            int committedBaseMipLevel = XRTexture2D.ResolveSparseCommittedBaseMipLevel(requestedBaseMipLevel, numSparseLevels, request.LogicalMipCount);
            int previousCommittedBaseMipLevel = Data.SparseTextureStreamingCommittedBaseMipLevel;
            bool hasPreviousCommit = previousCommittedBaseMipLevel != int.MaxValue;
            bool isDemotion = hasPreviousCommit && committedBaseMipLevel > previousCommittedBaseMipLevel;
            int tailFirstMipLevel = Math.Min(Math.Max(0, numSparseLevels), request.LogicalMipCount);
            SparseTextureStreamingPageSelection desiredPageSelection = request.PageSelection.Normalize();
            if (!desiredPageSelection.IsPartial || committedBaseMipLevel >= tailFirstMipLevel)
                desiredPageSelection = SparseTextureStreamingPageSelection.Full;

            SparseTextureStreamingPageSelection previousPageSelection = Data.SparseTextureStreamingResidentPageSelection.Normalize();

            if (!isDemotion)
            {
                CommitDesiredSparseCoverage(support, desiredPageSelection, committedBaseMipLevel, numSparseLevels, request.LogicalWidth, request.LogicalHeight, request.LogicalMipCount);
                UploadSparseResidentMipmaps(request, support, desiredPageSelection, numSparseLevels);
                SetSparseMipSamplingRange(requestedBaseMipLevel, request.LogicalMipCount - 1);
            }
            else
            {
                SetSparseMipSamplingRange(requestedBaseMipLevel, request.LogicalMipCount - 1);
                UncommitSparseMipRange(previousCommittedBaseMipLevel, committedBaseMipLevel, numSparseLevels, request.LogicalWidth, request.LogicalHeight);
            }

            UncommitSparseCoverageDifference(
                support,
                previousPageSelection,
                desiredPageSelection,
                previousCommittedBaseMipLevel,
                committedBaseMipLevel,
                numSparseLevels,
                request.LogicalWidth,
                request.LogicalHeight,
                request.LogicalMipCount);

            long committedBytes = XRTexture2D.EstimateSparsePageSelectionBytes(
                request.LogicalWidth,
                request.LogicalHeight,
                requestedBaseMipLevel,
                request.LogicalMipCount,
                numSparseLevels,
                support,
                desiredPageSelection,
                request.SizedInternalFormat);
            UpdateSparseCommittedBytes(committedBytes);
            UpdateSparseTextureState(request, desiredPageSelection, requestedBaseMipLevel, committedBaseMipLevel, numSparseLevels, committedBytes);

            return new SparseTextureStreamingTransitionResult(
                Applied: true,
                UsedSparseResidency: true,
                RequestedBaseMipLevel: requestedBaseMipLevel,
                CommittedBaseMipLevel: committedBaseMipLevel,
                NumSparseLevels: numSparseLevels,
                CommittedBytes: committedBytes);
        }
        catch (Exception ex)
        {
            Debug.OpenGLException(ex);
            return SparseTextureStreamingTransitionResult.Unsupported(ex.Message);
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

    private void CommitDesiredSparseCoverage(
        SparseTextureStreamingSupport support,
        SparseTextureStreamingPageSelection selection,
        int committedBaseMipLevel,
        int numSparseLevels,
        uint logicalWidth,
        uint logicalHeight,
        int logicalMipCount)
    {
        int tailFirstMipLevel = Math.Min(Math.Max(0, numSparseLevels), logicalMipCount);
        int individualMipEndExclusive = Math.Min(tailFirstMipLevel, logicalMipCount);
        for (int mipLevel = committedBaseMipLevel; mipLevel < individualMipEndExclusive; mipLevel++)
            CommitMipLevel(mipLevel, logicalWidth, logicalHeight, support, selection, commit: true);

        if (tailFirstMipLevel < logicalMipCount)
            CommitMipLevel(tailFirstMipLevel, logicalWidth, logicalHeight, commit: true);
    }

    private bool EnsureSparseStorageAllocated(
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingSupport support,
        out int numSparseLevels,
        out string? failureReason)
    {
        numSparseLevels = _sparseNumSparseLevels;
        failureReason = null;

        bool storageMatches =
            _sparseStorageAllocated
            && StorageSet
            && _sparseLogicalWidth == request.LogicalWidth
            && _sparseLogicalHeight == request.LogicalHeight
            && _sparseLogicalMipCount == request.LogicalMipCount;
        if (storageMatches)
        {
            numSparseLevels = _sparseNumSparseLevels;
            return true;
        }

        bool hadExistingStorage = StorageSet || _sparseStorageAllocated || _allocatedLevels > 0 || _allocatedVRAMBytes > 0 || _externalMemoryObject != 0;
        if (hadExistingStorage)
        {
            Destroy();
            Generate();
            Api.BindTexture(ToGLEnum(TextureTarget), BindingId);
            Renderer.SetBoundTexture(TextureTarget, this, Data.Name);
        }

        int sparseEnabled = 1;
        Api.TextureParameterI(BindingId, TextureSparseArb, in sparseEnabled);

        int pageIndex = Math.Max(0, support.VirtualPageSizeIndex);
        Api.TextureParameterI(BindingId, VirtualPageSizeIndexArb, in pageIndex);
        Api.TextureStorage2D(BindingId, (uint)request.LogicalMipCount, ToGLEnum(request.SizedInternalFormat), request.LogicalWidth, request.LogicalHeight);

        Api.GetTextureParameterI(BindingId, NumSparseLevelsArb, out numSparseLevels);
        if (numSparseLevels < 0)
            numSparseLevels = 0;

        StorageSet = true;
        _sparseStorageAllocated = true;
        _allocatedLevels = (uint)request.LogicalMipCount;
        _sparseLogicalWidth = request.LogicalWidth;
        _sparseLogicalHeight = request.LogicalHeight;
        _sparseLogicalMipCount = request.LogicalMipCount;
        _sparseNumSparseLevels = numSparseLevels;
        Data.SparseTextureStreamingEnabled = true;

        // Sparse immutable storage is a logical allocation only. Track only committed bytes
        // in VRAM stats, not the full logical mip chain.
        UpdateSparseCommittedBytes(0L);
        return true;
    }

    private void CommitSparseMipRange(int newCommittedBaseMipLevel, int previousCommittedBaseMipLevel, int numSparseLevels, uint logicalWidth, uint logicalHeight, int logicalMipCount)
    {
        int tailFirstMipLevel = Math.Min(Math.Max(0, numSparseLevels), logicalMipCount);
        bool tailExists = tailFirstMipLevel < logicalMipCount;

        if (tailExists && previousCommittedBaseMipLevel == int.MaxValue)
            CommitMipLevel(tailFirstMipLevel, logicalWidth, logicalHeight, commit: true);

        int individualCommitEndExclusive = tailExists ? tailFirstMipLevel : logicalMipCount;
        int previousBase = previousCommittedBaseMipLevel == int.MaxValue
            ? individualCommitEndExclusive
            : Math.Min(previousCommittedBaseMipLevel, individualCommitEndExclusive);

        for (int mipLevel = newCommittedBaseMipLevel; mipLevel < previousBase; mipLevel++)
            CommitMipLevel(mipLevel, logicalWidth, logicalHeight, commit: true);
    }

    private void UncommitSparseMipRange(int previousCommittedBaseMipLevel, int newCommittedBaseMipLevel, int numSparseLevels, uint logicalWidth, uint logicalHeight)
    {
        int tailFirstMipLevel = Math.Max(0, numSparseLevels);
        int uncommitEndExclusive = Math.Min(newCommittedBaseMipLevel, tailFirstMipLevel);
        for (int mipLevel = previousCommittedBaseMipLevel; mipLevel < uncommitEndExclusive; mipLevel++)
            CommitMipLevel(mipLevel, logicalWidth, logicalHeight, commit: false);
    }

    private void UncommitSparseCoverageDifference(
        SparseTextureStreamingSupport support,
        SparseTextureStreamingPageSelection previousSelection,
        SparseTextureStreamingPageSelection newSelection,
        int previousCommittedBaseMipLevel,
        int committedBaseMipLevel,
        int numSparseLevels,
        uint logicalWidth,
        uint logicalHeight,
        int logicalMipCount)
    {
        if (previousCommittedBaseMipLevel == int.MaxValue)
            return;

        int tailFirstMipLevel = Math.Min(Math.Max(0, numSparseLevels), logicalMipCount);
        int overlapStartMipLevel = Math.Max(committedBaseMipLevel, previousCommittedBaseMipLevel);
        int overlapEndMipLevel = Math.Min(tailFirstMipLevel, logicalMipCount);
        if (overlapStartMipLevel >= overlapEndMipLevel)
            return;

        for (int mipLevel = overlapStartMipLevel; mipLevel < overlapEndMipLevel; mipLevel++)
        {
            if (!XRTexture2D.TryResolveSparsePageRegion(support, previousSelection, logicalWidth, logicalHeight, mipLevel, out SparseTextureStreamingPageRegion previousRegion)
                || !previousRegion.HasArea
                || !XRTexture2D.TryResolveSparsePageRegion(support, newSelection, logicalWidth, logicalHeight, mipLevel, out SparseTextureStreamingPageRegion newRegion)
                || !newRegion.HasArea)
            {
                continue;
            }

            UncommitSparseRegionDifference(mipLevel, previousRegion, newRegion);
        }
    }

    private void UncommitSparseRegionDifference(int mipLevel, SparseTextureStreamingPageRegion previousRegion, SparseTextureStreamingPageRegion newRegion)
    {
        int previousRight = previousRegion.XOffset + (int)previousRegion.Width;
        int previousBottom = previousRegion.YOffset + (int)previousRegion.Height;
        int newRight = newRegion.XOffset + (int)newRegion.Width;
        int newBottom = newRegion.YOffset + (int)newRegion.Height;

        int intersectionLeft = Math.Max(previousRegion.XOffset, newRegion.XOffset);
        int intersectionTop = Math.Max(previousRegion.YOffset, newRegion.YOffset);
        int intersectionRight = Math.Min(previousRight, newRight);
        int intersectionBottom = Math.Min(previousBottom, newBottom);

        if (intersectionRight <= intersectionLeft || intersectionBottom <= intersectionTop)
        {
            TryUncommitSparseRegion(mipLevel, previousRegion.XOffset, previousRegion.YOffset, previousRegion.Width, previousRegion.Height);
            return;
        }

        if (previousRegion.XOffset < intersectionLeft)
        {
            TryUncommitSparseRegion(
                mipLevel,
                previousRegion.XOffset,
                previousRegion.YOffset,
                (uint)(intersectionLeft - previousRegion.XOffset),
                previousRegion.Height);
        }

        if (intersectionRight < previousRight)
        {
            TryUncommitSparseRegion(
                mipLevel,
                intersectionRight,
                previousRegion.YOffset,
                (uint)(previousRight - intersectionRight),
                previousRegion.Height);
        }

        uint middleWidth = (uint)Math.Max(0, intersectionRight - intersectionLeft);
        if (middleWidth == 0)
            return;

        if (previousRegion.YOffset < intersectionTop)
        {
            TryUncommitSparseRegion(
                mipLevel,
                intersectionLeft,
                previousRegion.YOffset,
                middleWidth,
                (uint)(intersectionTop - previousRegion.YOffset));
        }

        if (intersectionBottom < previousBottom)
        {
            TryUncommitSparseRegion(
                mipLevel,
                intersectionLeft,
                intersectionBottom,
                middleWidth,
                (uint)(previousBottom - intersectionBottom));
        }
    }

    private void TryUncommitSparseRegion(int mipLevel, int xOffset, int yOffset, uint width, uint height)
    {
        if (width == 0 || height == 0)
            return;

        if (!Renderer.TryCommitSparseTexturePages(ToGLEnum(TextureTarget), mipLevel, xOffset, yOffset, width, height, commit: false))
        {
            throw new InvalidOperationException($"glTexPageCommitmentARB is unavailable while attempting to uncommit sparse mip {mipLevel} region.");
        }
    }

    private void CommitMipLevel(int mipLevel, uint logicalWidth, uint logicalHeight, bool commit)
    {
        uint mipWidth = Math.Max(1u, logicalWidth >> mipLevel);
        uint mipHeight = Math.Max(1u, logicalHeight >> mipLevel);
        if (!Renderer.TryCommitSparseTexturePages(ToGLEnum(TextureTarget), mipLevel, mipWidth, mipHeight, commit))
        {
            throw new InvalidOperationException($"glTexPageCommitmentARB is unavailable while attempting to {(commit ? "commit" : "uncommit")} sparse mip {mipLevel}.");
        }
    }

    private void CommitMipLevel(
        int mipLevel,
        uint logicalWidth,
        uint logicalHeight,
        SparseTextureStreamingSupport support,
        SparseTextureStreamingPageSelection selection,
        bool commit)
    {
        if (!XRTexture2D.TryResolveSparsePageRegion(support, selection, logicalWidth, logicalHeight, mipLevel, out SparseTextureStreamingPageRegion region)
            || !region.HasArea)
        {
            return;
        }

        if (!Renderer.TryCommitSparseTexturePages(ToGLEnum(TextureTarget), mipLevel, region.XOffset, region.YOffset, region.Width, region.Height, commit))
        {
            throw new InvalidOperationException($"glTexPageCommitmentARB is unavailable while attempting to {(commit ? "commit" : "uncommit")} sparse mip {mipLevel} region.");
        }
    }

    private unsafe void UploadSparseResidentMipmaps(
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingSupport support,
        SparseTextureStreamingPageSelection selection,
        int numSparseLevels)
    {
        GLEnum target = ToGLEnum(TextureTarget);
        ResetUnpackStateForTextureUpload();
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
                Api.TexSubImage2D(
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

            PushWithData(
                target,
                mipLevel,
                mip.Width,
                mip.Height,
                ToGLEnum(mip.PixelFormat),
                ToGLEnum(mip.PixelType),
                ToInternalFormat(mip.InternalFormat),
                mipData,
                pbo: null,
                fullPush: false);
        }
    }

    private static unsafe DataSource CreateSparseMipRegionData(Mipmap2D mip, SparseTextureStreamingPageRegion region, uint bytesPerPixel)
    {
        DataSource? source = mip.Data;
        if (source is null || source.Length == 0 || !region.HasArea)
            return new DataSource(0u);

        uint sourceRowBytes = Math.Max(1u, mip.Width) * bytesPerPixel;
        uint regionRowBytes = region.Width * bytesPerPixel;
        DataSource copy = new(regionRowBytes * region.Height);
        for (uint row = 0; row < region.Height; row++)
        {
            uint sourceOffset = ((uint)region.YOffset + row) * sourceRowBytes + (uint)region.XOffset * bytesPerPixel;
            uint destOffset = row * regionRowBytes;
            Memory.Move(copy.Address + (int)destOffset, source.Address + (int)sourceOffset, regionRowBytes);
        }

        return copy;
    }

    private static uint GetSourceBytesPerPixel(Mipmap2D mip)
        => Math.Max(1u, XRTexture.ComponentSize(mip.PixelType) * (uint)XRTexture.GetComponentCount(mip.PixelFormat));

    private void SetSparseMipSamplingRange(int baseMipLevel, int maxMipLevel)
    {
        int clampedBaseMipLevel = Math.Max(0, baseMipLevel);
        int clampedMaxMipLevel = Math.Max(clampedBaseMipLevel, maxMipLevel);
        Api.TextureParameterI(BindingId, GLEnum.TextureBaseLevel, in clampedBaseMipLevel);
        Api.TextureParameterI(BindingId, GLEnum.TextureMaxLevel, in clampedMaxMipLevel);
    }

    private void UpdateSparseTextureState(
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingPageSelection selection,
        int requestedBaseMipLevel,
        int committedBaseMipLevel,
        int numSparseLevels,
        long committedBytes)
    {
        Data.SparseTextureStreamingEnabled = true;
        Data.SparseTextureStreamingLogicalWidth = request.LogicalWidth;
        Data.SparseTextureStreamingLogicalHeight = request.LogicalHeight;
        Data.SparseTextureStreamingLogicalMipCount = request.LogicalMipCount;
        Data.SparseTextureStreamingResidentBaseMipLevel = requestedBaseMipLevel;
        Data.SparseTextureStreamingCommittedBaseMipLevel = committedBaseMipLevel;
        Data.SparseTextureStreamingNumSparseLevels = numSparseLevels;
        Data.SparseTextureStreamingCommittedBytes = committedBytes;
        Data.SparseTextureStreamingResidentPageSelection = selection.Normalize();

        Data.Mipmaps = request.ResidentMipmaps;
        Data.AutoGenerateMipmaps = false;
        Data.Resizable = false;
        Data.SizedInternalFormat = request.SizedInternalFormat;
        Data.LargestMipmapLevel = requestedBaseMipLevel;
        Data.SmallestAllowedMipmapLevel = Math.Max(0, request.LogicalMipCount - 1);
        Data.MinFilter = request.ResidentMipmaps.Length > 1
            ? ETexMinFilter.LinearMipmapLinear
            : ETexMinFilter.Linear;
        Data.MagFilter = ETexMagFilter.Linear;

        ClearInvalidation();
    }

    private void UpdateSparseCommittedBytes(long committedBytes)
    {
        if (_allocatedVRAMBytes == committedBytes)
            return;

        if (_allocatedVRAMBytes > 0)
            Engine.Rendering.Stats.RemoveTextureAllocation(_allocatedVRAMBytes);

        _allocatedVRAMBytes = committedBytes;
        if (committedBytes > 0)
            Engine.Rendering.Stats.AddTextureAllocation(committedBytes);
    }

}
