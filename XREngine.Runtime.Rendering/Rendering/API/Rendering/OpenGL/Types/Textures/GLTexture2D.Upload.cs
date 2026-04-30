using Silk.NET.OpenGL;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL;

public partial class GLTexture2D
{
    private const PixelStoreParameter UnpackSkipRows = (PixelStoreParameter)0x0CF3;
    private const PixelStoreParameter UnpackSkipPixels = (PixelStoreParameter)0x0CF4;
    private const PixelStoreParameter UnpackSkipImages = (PixelStoreParameter)0x806D;
    private const PixelStoreParameter UnpackImageHeight = (PixelStoreParameter)0x806E;
    private const PixelStoreParameter UnpackSwapBytes = (PixelStoreParameter)0x0CF0;
    private const PixelStoreParameter UnpackLsbFirst = (PixelStoreParameter)0x0CF1;

    /// <summary>
    /// Any meaningful CPU-backed mip payload should be uploaded progressively rather
    /// than synchronously on first bind. Render-target/no-data textures still use the
    /// synchronous path, but asset textures defer uploads across frames.
    /// </summary>
    private const long ProgressivePushDataThresholdBytes = 4 * 1024;
    private const long ProgressiveMipUploadChunkBytes = 16 * 1024;

    private void ResetUnpackStateForTextureUpload()
        => ResetUnpackStateForTextureUpload(Api);

    private static void ResetUnpackStateForTextureUpload(GL gl)
    {
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        gl.PixelStore(UnpackSkipRows, 0);
        gl.PixelStore(UnpackSkipPixels, 0);
        gl.PixelStore(UnpackSkipImages, 0);
        gl.PixelStore(UnpackImageHeight, 0);
        gl.PixelStore(UnpackSwapBytes, 0);
        gl.PixelStore(UnpackLsbFirst, 0);
        gl.BindBuffer(GLEnum.PixelUnpackBuffer, 0);
    }

    /// <summary>
    /// Clears the invalidation flag and the NeedsFullPush flag on all mipmaps
    /// so that the next Bind() → VerifySettings() cycle does NOT call PushData().
    /// Call this after performing a raw GL upload (e.g. PBO-based video streaming)
    /// to prevent the engine from overwriting the texture with stale/null CPU data.
    /// </summary>
    public void ClearInvalidation()
    {
        IsInvalidated = false;
        foreach (MipmapInfo mip in _mipmaps)
            mip.NeedsFullPush = false;
    }

    public override unsafe void PushData()
    {
        if (IsPushing)
            return;

        try
        {
            using var sample = Engine.Profiler.Start("GLTexture2D.PushData");
            IsPushing = true;
            //Debug.Out($"Pushing texture: {GetDescribingName()}");
            OnPrePushData(out bool shouldPush, out bool allowPostPushCallback);
            if (!shouldPush)
            {
                if (allowPostPushCallback)
                    OnPostPushData();
                ClearProgressiveVisibleMipRange();
                IsPushing = false;
                return;
            }

            if (Data.SparseTextureStreamingEnabled
                && Data.SparseTextureStreamingResidentBaseMipLevel == int.MaxValue)
            {
                // Sparse storage has been prepared, but the shared-context upload has not
                // been exposed yet. Do not reinterpret stale CPU mip data at int.MaxValue.
                ClearInvalidation();
                IsPushing = false;
                return;
            }

            ApplyPendingImmutableStorageRecreate();
            Bind();

            GLEnum glTarget = ToGLEnum(TextureTarget);

            Api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

            EPixelInternalFormat? internalFormatForce;
            using (Engine.Profiler.Start("GLTexture2D.PushData.EnsureStorageAllocated"))
                internalFormatForce = EnsureStorageAllocated();

            // Determine whether mipmaps carry significant CPU data worth deferring.
            bool hasBulkMipData = false;
            if (Mipmaps is not null && Mipmaps.Length > 0)
            {
                long totalBytes = 0;
                for (int i = 0; i < Mipmaps.Length; ++i)
                {
                    DataSource? data = Mipmaps[i]?.Mipmap?.Data;
                    if (data is not null)
                        totalBytes += data.Length;
                }
                hasBulkMipData = totalBytes >= ProgressivePushDataThresholdBytes;
            }

            if (hasBulkMipData)
            {
                if (Data.RuntimeManagedProgressiveUploadActive)
                {
                    if (Data.RuntimeManagedProgressiveFinalizePending)
                    {
                        // Runtime upload has completed and restored the intended mip range.
                        // Apply final params now, then release runtime ownership in SetParameters.
                        ClearProgressiveVisibleMipRange();
                        IsInvalidated = false;
                        FinalizePushData(allowPostPushCallback);
                        return;
                    }

                    int mipLevelOffset = Data.SparseTextureStreamingEnabled
                        ? SparseTextureResidentBaseMipLevelOrZero
                        : 0;
                    int smallestResidentMip = Mipmaps!.Length - 1;
                    int lockMipLevel = Data.StreamingLockMipLevel;
                    int seedMipLevel = lockMipLevel >= 0 && lockMipLevel < Mipmaps.Length
                        ? lockMipLevel
                        : smallestResidentMip;

                    PushMipmap(glTarget, seedMipLevel + mipLevelOffset, Mipmaps[seedMipLevel], internalFormatForce);
                    if (!IsMultisampleTarget)
                    {
                        int seedLevel = seedMipLevel + mipLevelOffset;
                        SetProgressiveVisibleMipRange(seedLevel, seedLevel);
                    }

                    IsInvalidated = false;
                    FinalizePushData(allowPostPushCallback);
                    return;
                }

                // Large texture — defer per-mip uploads to a render-thread coroutine
                // so the current frame completes without a multi-hundred-ms stall.
                // Storage is already allocated above (fast); the texture will appear
                // black/transparent until the coroutine finishes uploading all mips.
                ScheduleProgressiveMipUpload(glTarget, internalFormatForce, allowPostPushCallback);
                Unbind();
                // IsPushing stays true — VerifySettings will skip re-entry until the coroutine clears it.
                return;
            }

            using (Engine.Profiler.Start("GLTexture2D.PushData.PushMipmaps"))
            {
                if (Mipmaps is null || Mipmaps.Length == 0)
                {
                    PushMipmap(glTarget, 0, null, internalFormatForce);
                }
                else
                {
                    int mipLevelOffset = Data.SparseTextureStreamingEnabled
                        ? SparseTextureResidentBaseMipLevelOrZero
                        : 0;
                    for (int i = 0; i < Mipmaps.Length; ++i)
                        PushMipmap(glTarget, i + mipLevelOffset, Mipmaps[i], internalFormatForce);
                }
            }

            ClearProgressiveVisibleMipRange();
            FinalizePushData(allowPostPushCallback);
        }
        catch (Exception ex)
        {
            Debug.OpenGLException(ex);
        }
        finally
        {
            if (IsPushing)
            {
                IsPushing = false;
                Unbind();
            }
        }
    }

    /// <summary>
    /// Common tail of <see cref="PushData"/>: applies mip-range parameters, LOD bounds,
    /// optional auto-mipmap generation, and fires the post-push callback.
    /// </summary>
    private void FinalizePushData(bool allowPostPushCallback)
    {
        int minLOD = -1000;
        int maxLOD = 1000;
        using (Engine.Profiler.Start("GLTexture2D.PushData.ApplyMipRangeParameters"))
            ApplyMipRangeParameters();

        if (!IsMultisampleTarget)
        {
            Api.TextureParameterI(BindingId, GLEnum.TextureMinLod, in minLOD);
            Api.TextureParameterI(BindingId, GLEnum.TextureMaxLod, in maxLOD);
        }

        if (Data.AutoGenerateMipmaps)
        {
            using var mipmapProf = Engine.Profiler.Start("GLTexture2D.PushData.GenerateMipmaps");
            GenerateMipmaps();
        }

        if (allowPostPushCallback)
            OnPostPushData();
    }

    /// <summary>
    /// Schedules a render-thread coroutine that uploads one mipmap level per tick,
    /// preventing large imported textures from stalling the render frame.
    /// </summary>
    private void ScheduleProgressiveMipUpload(GLEnum glTarget, EPixelInternalFormat? internalFormatForce, bool allowPostPushCallback)
    {
        int mipLevelOffset = Data.SparseTextureStreamingEnabled
            ? SparseTextureResidentBaseMipLevelOrZero
            : 0;

        int mipCount = Mipmaps!.Length;
        int smallestResidentMip = mipCount - 1;

        // Upload the smallest mip synchronously so the texture is never fully black
        // for an entire frame while the coroutine queues. A 1–4 px mip is negligible.
        PushMipmap(glTarget, smallestResidentMip + mipLevelOffset, Mipmaps[smallestResidentMip], internalFormatForce);
        if (!IsMultisampleTarget)
        {
            int seedBase = smallestResidentMip + mipLevelOffset;
            int seedMax = smallestResidentMip + mipLevelOffset;
            SetProgressiveVisibleMipRange(seedBase, seedMax);
            Api.TextureParameterI(BindingId, GLEnum.TextureBaseLevel, in seedBase);
            Api.TextureParameterI(BindingId, GLEnum.TextureMaxLevel, in seedMax);
        }

        // Start the coroutine one step above the already-uploaded smallest mip.
        int nextMip = smallestResidentMip - 1;
        int nextMipRow = 0;
        string textureName = string.IsNullOrWhiteSpace(Data.Name)
            ? BindingId.ToString()
            : Data.Name;
        string progressiveUploadLabel = $"GLTexture2D.ProgressiveMipUpload[{textureName}]";

        Engine.AddRenderThreadCoroutine(() =>
        {
            using var sample = Engine.Profiler.Start(progressiveUploadLabel);
            if (!IsGenerated)
            {
                ClearProgressiveVisibleMipRange();
                // Texture was destroyed before the coroutine finished — fire the
                // post-push callback so the streaming manager can clear the pending
                // transition rather than waiting for the stuck-transition timeout.
                IsPushing = false;
                if (allowPostPushCallback)
                    OnPostPushData();
                return true;
            }

            if (nextMip >= 0 && !IsMultisampleTarget)
            {
                int residentBaseLevel = Math.Min(nextMip + 1, smallestResidentMip) + mipLevelOffset;
                int residentMaxLevel = smallestResidentMip + mipLevelOffset;
                SetProgressiveVisibleMipRange(residentBaseLevel, residentMaxLevel);
            }
            else
            {
                ClearProgressiveVisibleMipRange();
            }

            Bind();
            Api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

            // Bind() → SetParameters → SetParametersInternal overwrites GL_TEXTURE_BASE_LEVEL
            // with Data.LargestMipmapLevel (often 0) before high-res mips are uploaded.
            // Re-enforce the clamped range so GL only samples fully-uploaded mips.
            if (nextMip >= 0 && !IsMultisampleTarget)
            {
                int enforcedBase = Math.Min(nextMip + 1, smallestResidentMip) + mipLevelOffset;
                int enforcedMax = smallestResidentMip + mipLevelOffset;
                Api.TextureParameterI(BindingId, GLEnum.TextureBaseLevel, in enforcedBase);
                Api.TextureParameterI(BindingId, GLEnum.TextureMaxLevel, in enforcedMax);
            }

            if (nextMip >= 0)
            {
                // Upload from smallest to largest so early frames only pay for tiny mips.
                // Clamp sampling to fully uploaded mips until the current mip is complete.
                // The persistent visible-range override keeps partial scanline uploads
                // invisible even if this texture is rebound later in the same frame.
                if (!TryPushProgressiveMipChunk(glTarget, nextMip + mipLevelOffset, Mipmaps[nextMip], internalFormatForce, ref nextMipRow))
                {
                    PushMipmap(glTarget, nextMip + mipLevelOffset, Mipmaps[nextMip], internalFormatForce);
                    nextMip--;
                    nextMipRow = 0;
                }
                else if (nextMipRow <= 0)
                {
                    nextMip--;
                }

                Unbind();
                return false; // Continue — more mips to upload.
            }

            // All mips uploaded — finalize.
            ClearProgressiveVisibleMipRange();
            FinalizePushData(allowPostPushCallback);
            IsPushing = false;
            Unbind();
            return true; // Done.
        }, progressiveUploadLabel);
    }

    private unsafe bool TryPushProgressiveMipChunk(
        GLEnum glTarget,
        int mipLevel,
        MipmapInfo info,
        EPixelInternalFormat? internalFormatForce,
        ref int nextRow)
    {
        int glLevel = mipLevel;
        if (!IsMipLevelInAllocatedRange(glLevel))
        {
            Debug.OpenGLWarning(
                $"[GLTexture2D] Skipping progressive mip upload for '{GetDescribingName()}', mip={mipLevel} (glLevel={glLevel}), " +
                $"allocatedLevels={_allocatedLevels}, sparseEnabled={Data.SparseTextureStreamingEnabled}, residentBase={Data.SparseTextureStreamingResidentBaseMipLevel}.");
            nextRow = 0;
            return false;
        }

        Mipmap2D mip = info.Mipmap;
        DataSource? data = mip.Data;
        XRDataBuffer? pbo = mip.StreamingPBO;
        if (data is null
            || data.Length <= 0
            || pbo is not null
            || Data.MultiSample
            || mip.Height == 0)
        {
            nextRow = 0;
            return false;
        }

        long rowBytes = data.Length / (long)mip.Height;
        if (rowBytes <= 0 || rowBytes * mip.Height != data.Length)
        {
            nextRow = 0;
            return false;
        }

        int remainingRows = (int)mip.Height - nextRow;
        if (remainingRows <= 0)
        {
            info.NeedsFullPush = false;
            nextRow = 0;
            return true;
        }

        int rowsPerChunk = Math.Clamp((int)(ProgressiveMipUploadChunkBytes / rowBytes), 1, remainingRows);

        GLEnum pixelFormat = ToGLEnum(mip.PixelFormat);
        GLEnum pixelType = ToGLEnum(mip.PixelType);
        InternalFormat internalPixelFormat = ToInternalFormat(internalFormatForce ?? mip.InternalFormat);

        if (!CanUseProgressiveChunkUpload(pixelFormat, pixelType))
        {
            if (nextRow == 0)
            {
                Debug.OpenGLWarning(
                    $"[GLTexture2D] Progressive chunk upload fallback to full mip upload for '{GetDescribingName()}', mip={mipLevel}, " +
                    $"size={mip.Width}x{mip.Height}, sized={Data.SizedInternalFormat}, internal={internalPixelFormat}, " +
                    $"pixelFormat={pixelFormat}, pixelType={pixelType}.");
            }

            nextRow = 0;
            return false;
        }

        using var uploadSample = Engine.Profiler.Start("GLTexture2D.PushMipmap.UploadChunk");
        PushWithDataRows(
            glTarget,
            glLevel,
            mip.Width,
            mip.Height,
            pixelFormat,
            pixelType,
            internalPixelFormat,
            data,
            nextRow,
            rowsPerChunk,
            info.NeedsFullPush);

        nextRow += rowsPerChunk;
        if (nextRow >= mip.Height)
        {
            info.NeedsFullPush = false;
            nextRow = 0;
        }

        return true;
    }

    private void PushPreparedMipLevel(int mipIndex)
    {
        if (!Engine.IsRenderThread)
        {
            string textureName = string.IsNullOrWhiteSpace(Data.Name) ? "UnnamedTexture" : Data.Name;
            Engine.EnqueueMainThreadTask(
                () => PushPreparedMipLevel(mipIndex),
                $"GLTexture2D.PushPreparedMipLevel[{textureName}].Mip{mipIndex}");
            return;
        }

        if (mipIndex < 0 || mipIndex >= Mipmaps.Length)
            return;

        ApplyPendingImmutableStorageRecreate();
        Generate();

        IGLTexture? previousTexture = Renderer.BoundTexture;
        bool restorePrevious = previousTexture is not null && !ReferenceEquals(previousTexture, this);

        Api.BindTexture(ToGLEnum(TextureTarget), BindingId);
        Renderer.SetBoundTexture(TextureTarget, this, Data.Name);

        try
        {
            ResetUnpackStateForTextureUpload();

            EPixelInternalFormat? internalFormatForce = EnsureStorageAllocated();
            int actualMipIndex = Data.SparseTextureStreamingEnabled
                ? mipIndex + SparseTextureResidentBaseMipLevelOrZero
                : mipIndex;
            PushMipmap(ToGLEnum(TextureTarget), actualMipIndex, Mipmaps[mipIndex], internalFormatForce);

            SetParameters();
            IsInvalidated = false;
        }
        catch (Exception ex)
        {
            Debug.OpenGLException(ex);
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

    private unsafe void PushMipmap(GLEnum glTarget, int mipIndex, MipmapInfo? info, EPixelInternalFormat? internalFormatForce)
    {
        using var sample = Engine.Profiler.Start("GLTexture2D.PushMipmap");
        int glLevel = mipIndex;
        if (!Data.Resizable && !StorageSet)
        {
            Debug.OpenGLWarning("Texture storage not set on non-resizable texture, can't push mipmaps.");
            return;
        }

        if (StorageSet && !IsMipLevelInAllocatedRange(glLevel))
        {
            uint mipWidth = info?.Mipmap?.Width ?? 0u;
            uint mipHeight = info?.Mipmap?.Height ?? 0u;
            Debug.OpenGLError(
                $"[GLTexture2D] Skipping mip upload outside allocated storage for '{GetDescribingName()}': " +
                $"binding={BindingId} mipIndex={mipIndex} glLevel={glLevel} mipmapDims={mipWidth}x{mipHeight} " +
                $"allocatedLevels={_allocatedLevels} allocatedDims={_allocatedWidth}x{_allocatedHeight} " +
                $"mipmapCount={Mipmaps?.Length ?? 0} SmallestAllowedMipmapLevel={Data.SmallestAllowedMipmapLevel} " +
                $"LargestMipmapLevel={Data.LargestMipmapLevel} StreamingLockMipLevel={Data.StreamingLockMipLevel} " +
                $"sized={Data.SizedInternalFormat} sparseEnabled={Data.SparseTextureStreamingEnabled} " +
                $"residentBase={Data.SparseTextureStreamingResidentBaseMipLevel} logicalMipCount={Data.SparseTextureStreamingLogicalMipCount}.");
            return;
        }

        GLEnum pixelFormat;
        GLEnum pixelType;
        InternalFormat internalPixelFormat;

        DataSource? data;
        bool fullPush;
        Mipmap2D? mip = info?.Mipmap;
        XRDataBuffer? pbo = null;
        if (mip is null)
        {
            // Allocate based on the texture's declared sized format.
            // This matters for render targets (depth/depth-stencil) that intentionally have no CPU mip data.
            internalPixelFormat = (InternalFormat)ToGLEnum(Data.SizedInternalFormat);

            switch (Data.SizedInternalFormat)
            {
                case ESizedInternalFormat.DepthComponent16:
                case ESizedInternalFormat.DepthComponent24:
                case ESizedInternalFormat.DepthComponent32f:
                    pixelFormat = GLEnum.DepthComponent;
                    pixelType = GLEnum.Float;
                    break;

                case ESizedInternalFormat.Depth24Stencil8:
                    pixelFormat = GLEnum.DepthStencil;
                    pixelType = GLEnum.UnsignedInt248;
                    break;

                case ESizedInternalFormat.Depth32fStencil8:
                    pixelFormat = GLEnum.DepthStencil;
                    pixelType = GLEnum.Float32UnsignedInt248Rev;
                    break;

                default:
                    pixelFormat = GLEnum.Rgba;
                    pixelType = GLEnum.UnsignedByte;
                    break;
            }

            data = null;
            // Textures that act as render targets frequently have no mip data.
            // For resizable textures, we still must allocate storage via TexImage*;
            // otherwise FBO attachment will be incomplete.
            fullPush = true;
        }
        else
        {
            pixelFormat = ToGLEnum(mip.PixelFormat);
            pixelType = ToGLEnum(mip.PixelType);
            internalPixelFormat = ToInternalFormat(internalFormatForce ?? mip.InternalFormat);
            data = mip.Data;
            pbo = mip.StreamingPBO;
            fullPush = info!.NeedsFullPush;
        }

        if ((data is not null && data.Length > 0) || pbo is not null)
        {
            using var uploadSample = Engine.Profiler.Start(fullPush ? "GLTexture2D.PushMipmap.UploadFull" : "GLTexture2D.PushMipmap.UploadSubImage");
            PushWithData(glTarget, glLevel, mip!.Width, mip.Height, pixelFormat, pixelType, internalPixelFormat, data, pbo, fullPush);
        }
        else
        {
            using var uploadSample = Engine.Profiler.Start("GLTexture2D.PushMipmap.AllocateNoData");
            uint width = Data.SparseTextureStreamingEnabled && Data.SparseTextureStreamingLogicalWidth > 0
                ? Data.SparseTextureStreamingLogicalWidth >> mipIndex
                : Data.Width >> mipIndex;
            uint height = Data.SparseTextureStreamingEnabled && Data.SparseTextureStreamingLogicalHeight > 0
                ? Data.SparseTextureStreamingLogicalHeight >> mipIndex
                : Data.Height >> mipIndex;
            PushWithNoData(glTarget, glLevel, width, height, pixelFormat, pixelType, internalPixelFormat, fullPush);
        }

        if (info is not null)
        {
            info.NeedsFullPush = false;
            //info.HasPushedUpdateData = true;
        }
    }

    private unsafe void PushWithNoData(
        GLEnum glTarget,
        int mipLevel,
        uint width,
        uint height,
        GLEnum pixelFormat,
        GLEnum pixelType,
        InternalFormat internalPixelFormat,
        bool fullPush)
    {
        if (!fullPush || !Data.Resizable)
            return;

        // Guard against 0-sized textures which cause GL_INVALID_VALUE.
        width = Math.Max(1u, width);
        height = Math.Max(1u, height);

        if (Data.MultiSample)
            Api.TexImage2DMultisample(glTarget, Data.MultiSampleCount, internalPixelFormat, width, height, Data.FixedSampleLocations);
        else
            Api.TexImage2D(glTarget, mipLevel, internalPixelFormat, width, height, 0, pixelFormat, pixelType, IntPtr.Zero.ToPointer());
    }

    /// <summary>
    /// Pushes the data to the texture.
    /// If data is null, the PBO is used to push the data.
    /// </summary>
    private unsafe void PushWithData(
        GLEnum glTarget,
        int mipLevel,
        uint width,
        uint height,
        GLEnum pixelFormat,
        GLEnum pixelType,
        InternalFormat internalPixelFormat,
        DataSource? data,
        XRDataBuffer? pbo,
        bool fullPush)
    {
        if (pbo is not null && pbo.Target != EBufferTarget.PixelUnpackBuffer)
            throw new ArgumentException("PBO must be of type PixelUnpackBuffer.");

        bool uploadParamsAdjusted = SanitizeUploadParams(
            mipLevel,
            width,
            height,
            internalPixelFormat,
            ref pixelFormat,
            ref pixelType);
        if (uploadParamsAdjusted)
        {
            Debug.OpenGLWarning(
                $"[GLTexture2D] Sanitized TexImage/TexSubImage upload parameters for '{GetDescribingName()}', mip={mipLevel}, " +
                $"size={width}x{height}, sized={Data.SizedInternalFormat}, internal={internalPixelFormat}, " +
                $"pixelFormat={pixelFormat}, pixelType={pixelType}, fullPush={fullPush}, hasPbo={pbo is not null}.");
        }

        // UI and other upload paths can leave GL unpack state behind. Reset all unpack
        // fields here so both CPU-pointer and PBO-backed mip uploads always read from the
        // expected origin with the expected row/image stride.
        ResetUnpackStateForTextureUpload();

        // If a non-zero named buffer object is bound to the GL_PIXEL_UNPACK_BUFFER target (see glBindBuffer) while a texture image is specified,
        // the data ptr is treated as a byte offset into the buffer object's data store.
        if (!fullPush || StorageSet)
        {
            if (data is null)
            {
                pbo?.Bind();
                Api.TexSubImage2D(glTarget, mipLevel, 0, 0, width, height, pixelFormat, pixelType, null);
                pbo?.Unbind();
            }
            else
            {
                Api.TexSubImage2D(glTarget, mipLevel, 0, 0, width, height, pixelFormat, pixelType, data.Address.Pointer);
            }
        }
        else if (Data.MultiSample)
        {
            if (data is not null)
                Debug.OpenGLWarning("Multisample textures do not support initial data, ignoring all mipmaps.");

            Api.TexImage2DMultisample(glTarget, Data.MultiSampleCount, internalPixelFormat, width, height, Data.FixedSampleLocations);
        }
        else if (data is not null)
        {
            Api.TexImage2D(glTarget, mipLevel, internalPixelFormat, width, height, 0, pixelFormat, pixelType, data.Address.Pointer);
        }
        else
        {
            pbo?.Bind();
            Api.TexImage2D(glTarget, mipLevel, internalPixelFormat, width, height, 0, pixelFormat, pixelType, null);
            pbo?.Unbind();
        }
    }

    private unsafe void PushWithDataRows(
        GLEnum glTarget,
        int mipLevel,
        uint width,
        uint height,
        GLEnum pixelFormat,
        GLEnum pixelType,
        InternalFormat internalPixelFormat,
        DataSource data,
        int startRow,
        int rowCount,
        bool fullPush)
    {
        if (rowCount <= 0)
            return;

        GLEnum originalPixelFormat = pixelFormat;
        GLEnum originalPixelType = pixelType;
        bool uploadParamsAdjusted = SanitizeUploadParams(
            mipLevel,
            width,
            height,
            internalPixelFormat,
            ref pixelFormat,
            ref pixelType);
        if (uploadParamsAdjusted && startRow == 0)
        {
            Debug.OpenGLWarning(
                $"[GLTexture2D] Sanitized TexSubImage2D row upload parameters for '{GetDescribingName()}', mip={mipLevel}, " +
                $"size={width}x{height}, rows={startRow}..{startRow + rowCount - 1}, sized={Data.SizedInternalFormat}, internal={internalPixelFormat}, " +
                $"pixelFormat={originalPixelFormat}->{pixelFormat}, pixelType={originalPixelType}->{pixelType}.");
        }

        if (fullPush && !StorageSet)
        {
            ResetUnpackStateForTextureUpload();
            Api.TexImage2D(glTarget, mipLevel, internalPixelFormat, width, height, 0, pixelFormat, pixelType, data.Address.Pointer);
            return;
        }

        long rowBytes = data.Length / (long)Math.Max(1u, height);
        byte* rowPointer = (byte*)data.Address.Pointer + (startRow * rowBytes);
        ResetUnpackStateForTextureUpload();
        /*
        Debug.OpenGLWarning(
            $"[GLTexture2D] TexSubImage2D PRE '{GetDescribingName()}' mip={mipLevel} size={width}x{height} rows={startRow}+{rowCount} " +
            $"format={pixelFormat} type={pixelType} internal={internalPixelFormat} sized={Data.SizedInternalFormat} storage={StorageSet}");
        */
        Api.TexSubImage2D(glTarget, mipLevel, 0, startRow, width, (uint)rowCount, pixelFormat, pixelType, rowPointer);
    }

    private bool CanUseProgressiveChunkUpload(GLEnum pixelFormat, GLEnum pixelType)
        => IsSupportedTexImagePixelFormat(pixelFormat) && IsSupportedTexImagePixelType(pixelType);

    private bool SanitizeUploadParams(
        int mipLevel,
        uint width,
        uint height,
        InternalFormat internalPixelFormat,
        ref GLEnum pixelFormat,
        ref GLEnum pixelType)
    {
        bool formatOk = IsSupportedTexImagePixelFormat(pixelFormat);
        bool typeOk = IsSupportedTexImagePixelType(pixelType);
        if (formatOk && typeOk)
            return false;

        (GLEnum fallbackFormat, GLEnum fallbackType) = GetSafeFallbackUploadParams();
        if (fallbackFormat == pixelFormat && fallbackType == pixelType)
            return false;

        Debug.OpenGLWarning(
            $"[GLTexture2D] Invalid upload format/type detected for '{GetDescribingName()}', mip={mipLevel}, size={width}x{height}, " +
            $"sized={Data.SizedInternalFormat}, internal={internalPixelFormat}, pixelFormat={pixelFormat}, pixelType={pixelType}. " +
            $"Using fallback pixelFormat={fallbackFormat}, pixelType={fallbackType}.");

        pixelFormat = fallbackFormat;
        pixelType = fallbackType;
        return true;
    }

    private (GLEnum PixelFormat, GLEnum PixelType) GetSafeFallbackUploadParams()
        => Data.SizedInternalFormat switch
        {
            ESizedInternalFormat.DepthComponent16 => (GLEnum.DepthComponent, GLEnum.UnsignedShort),
            ESizedInternalFormat.DepthComponent24 => (GLEnum.DepthComponent, GLEnum.UnsignedInt),
            ESizedInternalFormat.DepthComponent32f => (GLEnum.DepthComponent, GLEnum.Float),
            ESizedInternalFormat.Depth24Stencil8 => (GLEnum.DepthStencil, GLEnum.UnsignedInt248),
            ESizedInternalFormat.Depth32fStencil8 => (GLEnum.DepthStencil, GLEnum.Float32UnsignedInt248Rev),
            ESizedInternalFormat.StencilIndex8 => (GLEnum.StencilIndex, GLEnum.UnsignedByte),

            ESizedInternalFormat.R8i or
            ESizedInternalFormat.R16i or
            ESizedInternalFormat.R32i => (GLEnum.RedInteger, GLEnum.Int),
            ESizedInternalFormat.R8ui or
            ESizedInternalFormat.R16ui or
            ESizedInternalFormat.R32ui => (GLEnum.RedInteger, GLEnum.UnsignedInt),

            ESizedInternalFormat.Rg8i or
            ESizedInternalFormat.Rg16i or
            ESizedInternalFormat.Rg32i => (GLEnum.RGInteger, GLEnum.Int),
            ESizedInternalFormat.Rg8ui or
            ESizedInternalFormat.Rg16ui or
            ESizedInternalFormat.Rg32ui => (GLEnum.RGInteger, GLEnum.UnsignedInt),

            ESizedInternalFormat.Rgb8i or
            ESizedInternalFormat.Rgb16i or
            ESizedInternalFormat.Rgb32i => (GLEnum.RgbInteger, GLEnum.Int),
            ESizedInternalFormat.Rgb8ui or
            ESizedInternalFormat.Rgb16ui or
            ESizedInternalFormat.Rgb32ui => (GLEnum.RgbInteger, GLEnum.UnsignedInt),

            ESizedInternalFormat.Rgba8i or
            ESizedInternalFormat.Rgba16i or
            ESizedInternalFormat.Rgba32i => (GLEnum.RgbaInteger, GLEnum.Int),
            ESizedInternalFormat.Rgba8ui or
            ESizedInternalFormat.Rgba16ui or
            ESizedInternalFormat.Rgba32ui => (GLEnum.RgbaInteger, GLEnum.UnsignedInt),

            ESizedInternalFormat.R16f or
            ESizedInternalFormat.R32f => (GLEnum.Red, GLEnum.Float),
            ESizedInternalFormat.Rg16f or
            ESizedInternalFormat.Rg32f => (GLEnum.RG, GLEnum.Float),
            ESizedInternalFormat.Rgb16f or
            ESizedInternalFormat.Rgb32f or
            ESizedInternalFormat.R11fG11fB10f or
            ESizedInternalFormat.Rgb9E5 => (GLEnum.Rgb, GLEnum.Float),
            ESizedInternalFormat.Rgba16f or
            ESizedInternalFormat.Rgba32f => (GLEnum.Rgba, GLEnum.Float),

            ESizedInternalFormat.R8 or
            ESizedInternalFormat.R8Snorm or
            ESizedInternalFormat.R16 or
            ESizedInternalFormat.R16Snorm => (GLEnum.Red, GLEnum.UnsignedByte),
            ESizedInternalFormat.Rg8 or
            ESizedInternalFormat.Rg8Snorm or
            ESizedInternalFormat.Rg16 or
            ESizedInternalFormat.Rg16Snorm => (GLEnum.RG, GLEnum.UnsignedByte),
            ESizedInternalFormat.R3G3B2 or
            ESizedInternalFormat.Rgb4 or
            ESizedInternalFormat.Rgb5 or
            ESizedInternalFormat.Rgb8 or
            ESizedInternalFormat.Rgb8Snorm or
            ESizedInternalFormat.Rgb10 or
            ESizedInternalFormat.Rgb12 or
            ESizedInternalFormat.Rgb16Snorm or
            ESizedInternalFormat.Srgb8 => (GLEnum.Rgb, GLEnum.UnsignedByte),
            _ => (GLEnum.Rgba, GLEnum.UnsignedByte),
        };

    private static bool IsSupportedTexImagePixelFormat(GLEnum pixelFormat)
        => pixelFormat is
            GLEnum.Red or
            GLEnum.Green or
            GLEnum.Blue or
            GLEnum.Alpha or
            GLEnum.RG or
            GLEnum.Rgb or
            GLEnum.Bgr or
            GLEnum.Rgba or
            GLEnum.Bgra or
            GLEnum.RedInteger or
            GLEnum.GreenInteger or
            GLEnum.BlueInteger or
            GLEnum.RGInteger or
            GLEnum.RgbInteger or
            GLEnum.BgrInteger or
            GLEnum.RgbaInteger or
            GLEnum.BgraInteger or
            GLEnum.DepthComponent or
            GLEnum.DepthStencil or
            GLEnum.StencilIndex;

    private static bool IsSupportedTexImagePixelType(GLEnum pixelType)
        => pixelType is
            GLEnum.UnsignedByte or
            GLEnum.Byte or
            GLEnum.UnsignedShort or
            GLEnum.Short or
            GLEnum.UnsignedInt or
            GLEnum.Int or
            GLEnum.HalfFloat or
            GLEnum.Float or
            GLEnum.UnsignedByte332 or
            GLEnum.UnsignedShort565 or
            GLEnum.UnsignedShort4444 or
            GLEnum.UnsignedShort5551 or
            GLEnum.UnsignedInt8888 or
            GLEnum.UnsignedInt1010102 or
            GLEnum.UnsignedInt248 or
            GLEnum.UnsignedInt5999Rev or
            GLEnum.Float32UnsignedInt248Rev;
}
