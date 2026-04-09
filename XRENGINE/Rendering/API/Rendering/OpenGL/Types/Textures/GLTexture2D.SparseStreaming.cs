using Silk.NET.OpenGL;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class GLTexture2D
{
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

        IGLTexture? previousTexture = Renderer.BoundTexture;
        bool restorePrevious = previousTexture is not null && !ReferenceEquals(previousTexture, this);
        Api.BindTexture(ToGLEnum(TextureTarget), BindingId);
        Renderer.SetBoundTexture(TextureTarget, this, Data.Name);

        try
        {
            Api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

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
            int committedBaseMipLevel = ResolveCommittedBaseMipLevel(requestedBaseMipLevel, numSparseLevels, request.LogicalMipCount);
            int previousCommittedBaseMipLevel = Data.SparseTextureStreamingCommittedBaseMipLevel;
            bool hasPreviousCommit = previousCommittedBaseMipLevel != int.MaxValue;
            bool isDemotion = hasPreviousCommit && committedBaseMipLevel > previousCommittedBaseMipLevel;

            if (!isDemotion)
            {
                CommitSparseMipRange(committedBaseMipLevel, previousCommittedBaseMipLevel, numSparseLevels, request.LogicalWidth, request.LogicalHeight, request.LogicalMipCount);
                UploadSparseResidentMipmaps(request);
                SetSparseMipSamplingRange(requestedBaseMipLevel, request.LogicalMipCount - 1);
            }
            else
            {
                SetSparseMipSamplingRange(requestedBaseMipLevel, request.LogicalMipCount - 1);
                UncommitSparseMipRange(previousCommittedBaseMipLevel, committedBaseMipLevel, numSparseLevels, request.LogicalWidth, request.LogicalHeight);
            }

            long committedBytes = XRTexture2D.EstimateMipRangeBytes(
                request.LogicalWidth,
                request.LogicalHeight,
                committedBaseMipLevel,
                request.LogicalMipCount,
                request.SizedInternalFormat);
            UpdateSparseCommittedBytes(committedBytes);
            UpdateSparseTextureState(request, requestedBaseMipLevel, committedBaseMipLevel, numSparseLevels, committedBytes);

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
        Api.TextureParameterI(BindingId, GLEnum.TextureSparseArb, in sparseEnabled);

        int pageIndex = Math.Max(0, support.VirtualPageSizeIndex);
        Api.TextureParameterI(BindingId, GLEnum.VirtualPageSizeIndexArb, in pageIndex);
        Api.TextureStorage2D(BindingId, (uint)request.LogicalMipCount, ToGLEnum(request.SizedInternalFormat), request.LogicalWidth, request.LogicalHeight);

        Api.GetTextureParameterI(BindingId, GLEnum.NumSparseLevelsArb, out numSparseLevels);
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

    private void CommitMipLevel(int mipLevel, uint logicalWidth, uint logicalHeight, bool commit)
    {
        uint mipWidth = Math.Max(1u, logicalWidth >> mipLevel);
        uint mipHeight = Math.Max(1u, logicalHeight >> mipLevel);
        if (!Renderer.TryCommitSparseTexturePages(ToGLEnum(TextureTarget), mipLevel, mipWidth, mipHeight, commit))
        {
            throw new InvalidOperationException($"glTexPageCommitmentARB is unavailable while attempting to {(commit ? "commit" : "uncommit")} sparse mip {mipLevel}.");
        }
    }

    private void UploadSparseResidentMipmaps(SparseTextureStreamingTransitionRequest request)
    {
        GLEnum target = ToGLEnum(TextureTarget);
        Mipmap2D[] residentMipmaps = request.ResidentMipmaps;
        for (int i = 0; i < residentMipmaps.Length; i++)
        {
            Mipmap2D mip = residentMipmaps[i];
            DataSource? mipData = mip.Data;
            if (mipData is null || mipData.Length == 0)
                continue;

            PushWithData(
                target,
                request.RequestedBaseMipLevel + i,
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

    private void SetSparseMipSamplingRange(int baseMipLevel, int maxMipLevel)
    {
        int clampedBaseMipLevel = Math.Max(0, baseMipLevel);
        int clampedMaxMipLevel = Math.Max(clampedBaseMipLevel, maxMipLevel);
        Api.TextureParameterI(BindingId, GLEnum.TextureBaseLevel, in clampedBaseMipLevel);
        Api.TextureParameterI(BindingId, GLEnum.TextureMaxLevel, in clampedMaxMipLevel);
    }

    private void UpdateSparseTextureState(
        SparseTextureStreamingTransitionRequest request,
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

    private static int ResolveCommittedBaseMipLevel(int requestedBaseMipLevel, int numSparseLevels, int logicalMipCount)
    {
        if (logicalMipCount <= 0)
            return 0;

        int tailFirstMipLevel = Math.Min(Math.Max(0, numSparseLevels), logicalMipCount);
        if (tailFirstMipLevel >= logicalMipCount)
            return Math.Clamp(requestedBaseMipLevel, 0, logicalMipCount - 1);

        return Math.Min(Math.Clamp(requestedBaseMipLevel, 0, logicalMipCount - 1), tailFirstMipLevel);
    }
}
