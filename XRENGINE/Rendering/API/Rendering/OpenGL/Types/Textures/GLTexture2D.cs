using XREngine.Extensions;
using Silk.NET.OpenGL;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public partial class GLTexture2D(OpenGLRenderer renderer, XRTexture2D data) : GLTexture<XRTexture2D>(renderer, data)
    {
        private const string TextureFilterAnisotropicExtension = "GL_EXT_texture_filter_anisotropic";
        private const float DesiredMaxAnisotropy = 8.0f;
        private const GLEnum TextureMaxAnisotropyExt = (GLEnum)0x84FE;
        private const GLEnum MaxTextureMaxAnisotropyExt = (GLEnum)0x84FF;

        /// <summary>
        /// Any meaningful CPU-backed mip payload should be uploaded progressively rather
        /// than synchronously on first bind. Render-target/no-data textures still use the
        /// synchronous path, but asset textures defer uploads across frames.
        /// </summary>
        private const long ProgressivePushDataThresholdBytes = 4 * 1024;
        private const long ProgressiveMipUploadChunkBytes = 64 * 1024;

        private static bool? _supportsTextureFilterAnisotropic;
        private static float _maxSupportedTextureAnisotropy = 1.0f;
        /// <summary>
        /// Clears the invalidation flag and the NeedsFullPush flag on all mipmaps
        /// so that the next Bind() → VerifySettings() cycle does NOT call PushData().
        /// Call this after performing a raw GL upload (e.g. PBO-based video streaming)
        /// to prevent the engine from overwriting the texture with stale/null CPU data.
        /// </summary>
        public void ClearInvalidation()
        {
            IsInvalidated = false;
            foreach (var mip in _mipmaps)
                mip.NeedsFullPush = false;
        }

        private MipmapInfo[] _mipmaps = [];
        public MipmapInfo[] Mipmaps
        {
            get => _mipmaps;
            private set => SetField(ref _mipmaps, value);
        }

        private bool _storageSet = false;
        public bool StorageSet
        {
            get => _storageSet;
            private set => SetField(ref _storageSet, value);
        }

        /// <summary>
        /// Tracks the currently allocated GPU memory size for this texture in bytes.
        /// </summary>
        private long _allocatedVRAMBytes = 0;
        private uint _allocatedLevels = 0;
        private uint _allocatedWidth = 0;
        private uint _allocatedHeight = 0;
        private ESizedInternalFormat _allocatedInternalFormat;
        private volatile bool _pendingImmutableStorageRecreate;
        private uint _externalMemoryObject;

        protected override void DataPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.DataPropertyChanged(sender, e);
            switch (e.PropertyName)
            {
                case nameof(XRTexture2D.Mipmaps):
                    UpdateMipmaps();
                    break;
                case nameof(XRTexture2D.SizedInternalFormat):
                    // Immutable storage format must match; destroy the GL handle when it changes.
                    if (StorageSet && Data.SizedInternalFormat != _allocatedInternalFormat)
                        DataResized();
                    break;
            }
        }

        private void UpdateMipmaps()
        {
            Mipmap2D[] sourceMipmaps = Data.Mipmaps.Where(static mip => mip is not null).ToArray();

            // If immutable storage was already allocated at different dimensions or with
            // fewer mip levels, the GL name must be destroyed so EnsureStorageAllocated
            // creates fresh storage.  Without this, TexSubImage2D would receive
            // width/height that exceed the allocated storage and produce GL_INVALID_VALUE,
            // or mip levels beyond the allocated count would fail with
            // "Invalid texture format".
            if (StorageSet && sourceMipmaps.Length > 0)
            {
                uint newWidth = sourceMipmaps[0].Width;
                uint newHeight = sourceMipmaps[0].Height;
                if (newWidth != _allocatedWidth
                    || newHeight != _allocatedHeight
                    || (uint)sourceMipmaps.Length > _allocatedLevels)
                    DataResized();
            }

            Mipmaps = new MipmapInfo[sourceMipmaps.Length];
            for (int i = 0; i < sourceMipmaps.Length; ++i)
                Mipmaps[i] = new MipmapInfo(this, sourceMipmaps[i]);
            Invalidate();
        }

        public override ETextureTarget TextureTarget
            => Data.MultiSample 
            ? ETextureTarget.Texture2DMultisample
            : ETextureTarget.Texture2D;

        protected override void UnlinkData()
        {
            base.UnlinkData();

            Data.Resized -= DataResized;
            Data.PushMipLevelRequested -= PushPreparedMipLevel;
            Data.SparseTextureStreamingTransitionRequested -= ApplySparseTextureStreamingTransition;
            Mipmaps = [];
        }
        protected override void LinkData()
        {
            base.LinkData();

            Data.Resized += DataResized;
            Data.PushMipLevelRequested += PushPreparedMipLevel;
            Data.SparseTextureStreamingTransitionRequested += ApplySparseTextureStreamingTransition;
            UpdateMipmaps();
        }

        private void DataResized()
        {
            bool requiresImmutableRecreate = !Data.Resizable && (StorageSet || _pendingImmutableStorageRecreate);
            StorageSet = false;
            _allocatedLevels = 0;
            _allocatedWidth = 0;
            _allocatedHeight = 0;
            _allocatedInternalFormat = default;
            _sparseStorageAllocated = false;
            _sparseLogicalWidth = 0;
            _sparseLogicalHeight = 0;
            _sparseLogicalMipCount = 0;
            _sparseNumSparseLevels = 0;
            Data.ClearSparseTextureStreamingState();
            Mipmaps.ForEach(m =>
            {
                m.NeedsFullPush = true;
                //m.HasPushedUpdateData = false;
            });

            // Immutable storage (glTextureStorage2D) cannot be re-allocated on the same
            // GL name. If this resize arrives off the render thread, defer the destroy
            // until the next upload entry point to avoid racing with a bind that still
            // targets the old immutable texture object.
            if (requiresImmutableRecreate)
            {
                if (Engine.IsRenderThread)
                {
                    _pendingImmutableStorageRecreate = false;
                    Destroy();
                }
                else
                {
                    _pendingImmutableStorageRecreate = true;
                    Invalidate();
                }
            }
            else
                Invalidate();
        }

        private void ApplyPendingImmutableStorageRecreate()
        {
            if (!_pendingImmutableStorageRecreate || !Engine.IsRenderThread)
                return;

            _pendingImmutableStorageRecreate = false;
            if (IsGenerated)
                Destroy();
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
                    IsPushing = false;
                    return;
                }

                ApplyPendingImmutableStorageRecreate();
                Bind();

                var glTarget = ToGLEnum(TextureTarget);

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
                        var d = Mipmaps[i]?.Mipmap?.Data;
                        if (d is not null)
                            totalBytes += d.Length;
                    }
                    hasBulkMipData = totalBytes >= ProgressivePushDataThresholdBytes;
                }

                if (hasBulkMipData)
                {
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
                        PushMipmap(glTarget, 0, null, internalFormatForce);
                    else
                    {
                        int mipLevelOffset = Data.SparseTextureStreamingEnabled
                            ? Math.Max(0, Data.SparseTextureStreamingResidentBaseMipLevel)
                            : 0;
                        for (int i = 0; i < Mipmaps.Length; ++i)
                            PushMipmap(glTarget, i + mipLevelOffset, Mipmaps[i], internalFormatForce);
                    }
                }

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
                ? Math.Max(0, Data.SparseTextureStreamingResidentBaseMipLevel)
                : 0;

            int mipCount = Mipmaps!.Length;
            int smallestResidentMip = mipCount - 1;

            // Upload the smallest mip synchronously so the texture is never fully black
            // for an entire frame while the coroutine queues.  A 1–4 px mip is negligible.
            PushMipmap(glTarget, smallestResidentMip + mipLevelOffset, Mipmaps![smallestResidentMip], internalFormatForce);
            if (!IsMultisampleTarget)
            {
                int seedBase = smallestResidentMip + mipLevelOffset;
                int seedMax  = smallestResidentMip + mipLevelOffset;
                Api.TextureParameterI(BindingId, GLEnum.TextureBaseLevel, in seedBase);
                Api.TextureParameterI(BindingId, GLEnum.TextureMaxLevel,  in seedMax);
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
                    // Texture was destroyed before the coroutine finished — fire the
                    // post-push callback so the streaming manager can clear the pending
                    // transition rather than waiting for the stuck-transition timeout.
                    IsPushing = false;
                    if (allowPostPushCallback)
                        OnPostPushData();
                    return true;
                }

                Bind();
                Api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

                if (nextMip >= 0)
                {
                    // Upload from smallest to largest so early frames only pay for tiny mips.
                    // Clamp sampling to fully uploaded mips until the current mip is complete.
                    if (!IsMultisampleTarget)
                    {
                        int residentBaseLevel = Math.Min(nextMip + 1, smallestResidentMip) + mipLevelOffset;
                        int residentMaxLevel = smallestResidentMip + mipLevelOffset;
                        Api.TextureParameterI(BindingId, GLEnum.TextureBaseLevel, in residentBaseLevel);
                        Api.TextureParameterI(BindingId, GLEnum.TextureMaxLevel, in residentMaxLevel);
                    }

                    if (!TryPushProgressiveMipChunk(glTarget, nextMip + mipLevelOffset, Mipmaps![nextMip], internalFormatForce, ref nextMipRow))
                    {
                        PushMipmap(glTarget, nextMip + mipLevelOffset, Mipmaps![nextMip], internalFormatForce);
                        nextMip--;
                        nextMipRow = 0;
                    }
                    else if (nextMipRow <= 0)
                    {
                        nextMip--;
                    }

                    Unbind();
                    return false; // continue — more mips to upload
                }

                // All mips uploaded — finalize.
                FinalizePushData(allowPostPushCallback);
                IsPushing = false;
                Unbind();
                return true; // done
            }, progressiveUploadLabel);
        }

        private unsafe bool TryPushProgressiveMipChunk(
            GLEnum glTarget,
            int mipLevel,
            MipmapInfo info,
            EPixelInternalFormat? internalFormatForce,
            ref int nextRow)
        {
            int glLevel = mipLevel - (Data.SparseTextureStreamingEnabled ? Data.SparseTextureStreamingResidentBaseMipLevel : 0);
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

        private EPixelInternalFormat? EnsureStorageAllocated()
        {
            EPixelInternalFormat? internalFormatForce = null;
            if (!Data.Resizable && !StorageSet)
            {
                uint w = Math.Max(1u, Data.Width);
                uint h = Math.Max(1u, Data.Height);
                int requestedLevels = Math.Max(1, Data.SmallestMipmapLevel + 1);
                if (Data.SparseTextureStreamingEnabled)
                {
                    int residentBaseMipLevel = Math.Max(0, Data.SparseTextureStreamingResidentBaseMipLevel);
                    int residentMipCount = Math.Max(0, Mipmaps?.Length ?? 0);
                    int logicalMipCount = Math.Max(0, Data.SparseTextureStreamingLogicalMipCount);

                    // Sparse uploads target logical mip indices (resident base + local mip index).
                    // Storage must include the full addressed level range, not just resident mip count.
                    int requiredSparseLevels = logicalMipCount > 0
                        ? logicalMipCount
                        : residentBaseMipLevel + residentMipCount;
                    requestedLevels = Math.Max(requestedLevels, Math.Max(1, requiredSparseLevels));
                }

                uint levels = (uint)requestedLevels;
                long requestedBytes = CalculateTextureVRAMSize(w, h, levels, Data.SizedInternalFormat, Data.MultiSample ? Data.MultiSampleCount : 1u);
                if (!Engine.Rendering.Stats.CanAllocateVram(requestedBytes, _allocatedVRAMBytes, out long projectedBytes, out long budgetBytes))
                {
                    Debug.OpenGLWarning($"[VRAM Budget] Skipping 2D texture allocation for '{Data.Name ?? BindingId.ToString()}' ({requestedBytes} bytes). Projected={projectedBytes} bytes, Budget={budgetBytes} bytes.");
                    return null;
                }

                if (_allocatedVRAMBytes > 0)
                {
                    Engine.Rendering.Stats.RemoveTextureAllocation(_allocatedVRAMBytes);
                    _allocatedVRAMBytes = 0;
                }

                if (_externalMemoryObject != 0)
                {
                    Renderer.DeleteMemoryObject(_externalMemoryObject);
                    _externalMemoryObject = 0;
                }

                if (Data.UsesOpenGlExternalMemoryImport)
                {
                    unsafe
                    {
                        _externalMemoryObject = Renderer.CreateImportedMemoryObject(
                            Data.OpenGlExternalMemoryImportSize,
                            (void*)Data.OpenGlExternalMemoryImportHandle);
                    }

                    if (_externalMemoryObject == 0)
                        throw new InvalidOperationException($"Failed to import external memory for texture '{Data.OpenGlExternalMemoryLabel ?? Data.Name ?? BindingId.ToString()}'.");

                    var sizedInternalFormat = (Silk.NET.OpenGLES.SizedInternalFormat)(uint)ToGLEnum(Data.SizedInternalFormat);
                    if (Data.MultiSample)
                    {
                        Renderer.EXTMemoryObject?.TextureStorageMem2DMultisample(
                            BindingId,
                            Data.MultiSampleCount,
                            sizedInternalFormat,
                            w,
                            h,
                            Data.FixedSampleLocations,
                            _externalMemoryObject,
                            0);
                    }
                    else
                    {
                        Renderer.EXTMemoryObject?.TextureStorageMem2D(
                            BindingId,
                            levels,
                            sizedInternalFormat,
                            w,
                            h,
                            _externalMemoryObject,
                            0);
                    }
                }
                else if (Data.MultiSample)
                    Api.TextureStorage2DMultisample(BindingId, Data.MultiSampleCount, ToGLEnum(Data.SizedInternalFormat), w, h, Data.FixedSampleLocations);
                else
                    Api.TextureStorage2D(BindingId, levels, ToGLEnum(Data.SizedInternalFormat), w, h);

                internalFormatForce = ToBaseInternalFormat(Data.SizedInternalFormat);
                StorageSet = true;
                _allocatedLevels = levels;
                _allocatedWidth = w;
                _allocatedHeight = h;
                _allocatedInternalFormat = Data.SizedInternalFormat;

                _allocatedVRAMBytes = CalculateTextureVRAMSize(w, h, levels, Data.SizedInternalFormat, Data.MultiSample ? Data.MultiSampleCount : 1u);
                Engine.Rendering.Stats.AddTextureAllocation(_allocatedVRAMBytes);
            }

            return internalFormatForce;
        }

        private int ResolveMaxMipLevel(int baseLevel)
        {
            if (IsMultisampleTarget)
                return baseLevel;

            if (Data.SparseTextureStreamingEnabled && Data.SparseTextureStreamingLogicalMipCount > 0)
            {
                int sparseConfiguredMaxLevel = Math.Max(baseLevel, Data.SmallestAllowedMipmapLevel);
                int logicalMaxLevel = Math.Max(baseLevel, Data.SparseTextureStreamingLogicalMipCount - 1);
                int sparseAllocatedMaxLevel = _allocatedLevels > 0
                    ? Math.Max(baseLevel, (int)_allocatedLevels - 1)
                    : logicalMaxLevel;
                return Math.Max(baseLevel, Math.Min(sparseAllocatedMaxLevel, Math.Max(sparseConfiguredMaxLevel, logicalMaxLevel)));
            }

            int configuredMaxLevel = Math.Max(baseLevel, Data.SmallestAllowedMipmapLevel);
            int naturalMaxLevel = Data.SmallestMipmapLevel;
            int allocatedMaxLevel = _allocatedLevels > 0
                ? Math.Max(baseLevel, (int)_allocatedLevels - 1)
                : naturalMaxLevel; // Mutable storage: glGenerateMipmap/texelFetch can use up to the natural max.

            if (Data.AutoGenerateMipmaps)
                return Math.Max(baseLevel, Math.Min(allocatedMaxLevel, naturalMaxLevel));

            // When multiple Mipmaps entries exist they represent actual uploaded
            // mip data, so cap to the available count.  A single entry (the default
            // from CreateFrameBufferTexture and similar) is just the mip-0 descriptor
            // and must NOT clamp GL_TEXTURE_MAX_LEVEL — immutable-storage FBO textures
            // allocate their full mip chain via glTexStorage2D and write individual
            // levels through FBO render targets.
            if (Mipmaps is not null && Mipmaps.Length > 1)
                return Math.Max(baseLevel, Math.Min(allocatedMaxLevel, Math.Min(Mipmaps.Length - 1, configuredMaxLevel)));

            return Math.Max(baseLevel, Math.Min(allocatedMaxLevel, configuredMaxLevel));
        }

        private void ApplyMipRangeParameters()
        {
            int baseLevel = Math.Max(0, Data.LargestMipmapLevel);
            int maxLevel = ResolveMaxMipLevel(baseLevel);

            Api.TextureParameterI(BindingId, GLEnum.TextureBaseLevel, in baseLevel);
            Api.TextureParameterI(BindingId, GLEnum.TextureMaxLevel, in maxLevel);
        }

        private bool TryGetSupportedTextureAnisotropy(out float maxSupportedAnisotropy)
        {
            if (_supportsTextureFilterAnisotropic.HasValue)
            {
                maxSupportedAnisotropy = _maxSupportedTextureAnisotropy;
                return _supportsTextureFilterAnisotropic.Value;
            }

            string[] extensions = Engine.Rendering.State.OpenGLExtensions;
            bool supported = Array.IndexOf(extensions, TextureFilterAnisotropicExtension) >= 0;
            float driverMax = 1.0f;
            if (supported)
            {
                try
                {
                    driverMax = MathF.Max(1.0f, Api.GetFloat(MaxTextureMaxAnisotropyExt));
                }
                catch
                {
                    supported = false;
                }
            }

            _supportsTextureFilterAnisotropic = supported;
            _maxSupportedTextureAnisotropy = driverMax;
            maxSupportedAnisotropy = driverMax;
            return supported;
        }

        private static bool UsesMipmapFiltering(ETexMinFilter minFilter)
            => minFilter is
                ETexMinFilter.NearestMipmapNearest or
                ETexMinFilter.LinearMipmapNearest or
                ETexMinFilter.NearestMipmapLinear or
                ETexMinFilter.LinearMipmapLinear;

        private void ApplyTextureAnisotropy()
        {
            if (!TryGetSupportedTextureAnisotropy(out float maxSupportedAnisotropy))
                return;

            float anisotropy = UsesMipmapFiltering(Data.MinFilter)
                ? MathF.Min(maxSupportedAnisotropy, DesiredMaxAnisotropy)
                : 1.0f;

            Api.TextureParameter(BindingId, TextureMaxAnisotropyExt, anisotropy);
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

            var previousTexture = Renderer.BoundTexture;
            bool restorePrevious = previousTexture is not null && !ReferenceEquals(previousTexture, this);

            Api.BindTexture(ToGLEnum(TextureTarget), BindingId);
            Renderer.SetBoundTexture(TextureTarget, this, Data.Name);

            try
            {
                Api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

                EPixelInternalFormat? internalFormatForce = EnsureStorageAllocated();
                int actualMipIndex = Data.SparseTextureStreamingEnabled
                    ? mipIndex + Math.Max(0, Data.SparseTextureStreamingResidentBaseMipLevel)
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

        private unsafe void PushMipmap(GLEnum glTarget, int i, MipmapInfo? info, EPixelInternalFormat? internalFormatForce)
        {
            using var sample = Engine.Profiler.Start("GLTexture2D.PushMipmap");
            int glLevel = i - (Data.SparseTextureStreamingEnabled ? Data.SparseTextureStreamingResidentBaseMipLevel : 0);
            if (!Data.Resizable && !StorageSet)
            {
                Debug.OpenGLWarning("Texture storage not set on non-resizable texture, can't push mipmaps.");
                return;
            }

            if (StorageSet && !IsMipLevelInAllocatedRange(glLevel))
            {
                Debug.OpenGLWarning(
                    $"[GLTexture2D] Skipping mip upload outside allocated storage for '{GetDescribingName()}': mip={i}, allocatedLevels={_allocatedLevels}, " +
                    $"sized={Data.SizedInternalFormat}, sparseEnabled={Data.SparseTextureStreamingEnabled}, residentBase={Data.SparseTextureStreamingResidentBaseMipLevel}, logicalMipCount={Data.SparseTextureStreamingLogicalMipCount}.");
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
                PushWithNoData(glTarget, glLevel, Data.Width >> i, Data.Height >> i, pixelFormat, pixelType, internalPixelFormat, fullPush);
            }

            if (info != null)
            {
                info.NeedsFullPush = false;
                //info.HasPushedUpdateData = true;
            }
        }

        private unsafe void PushWithNoData(
            GLEnum glTarget,
            int i,
            uint w,
            uint h,
            GLEnum pixelFormat,
            GLEnum pixelType,
            InternalFormat internalPixelFormat,
            bool fullPush)
        {
            if (!fullPush || !Data.Resizable)
                return;

            // Guard against 0-sized textures which cause GL_INVALID_VALUE.
            w = Math.Max(1u, w);
            h = Math.Max(1u, h);
            
            if (Data.MultiSample)
                Api.TexImage2DMultisample(glTarget, Data.MultiSampleCount, internalPixelFormat, w, h, Data.FixedSampleLocations);
            else
                Api.TexImage2D(glTarget, i, internalPixelFormat, w, h, 0, pixelFormat, pixelType, IntPtr.Zero.ToPointer());
        }

        /// <summary>
        /// Pushes the data to the texture.
        /// If data is null, the PBO is used to push the data.
        /// </summary>
        /// <param name="glTarget"></param>
        /// <param name="i"></param>
        /// <param name="w"></param>
        /// <param name="h"></param>
        /// <param name="pixelFormat"></param>
        /// <param name="pixelType"></param>
        /// <param name="internalPixelFormat"></param>
        /// <param name="data"></param>
        /// <param name="fullPush"></param>
        private unsafe void PushWithData(
            GLEnum glTarget,
            int i,
            uint w,
            uint h,
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
                i,
                w,
                h,
                internalPixelFormat,
                ref pixelFormat,
                ref pixelType);
            if (uploadParamsAdjusted)
            {
                Debug.OpenGLWarning(
                    $"[GLTexture2D] Sanitized TexImage/TexSubImage upload parameters for '{GetDescribingName()}', mip={i}, " +
                    $"size={w}x{h}, sized={Data.SizedInternalFormat}, internal={internalPixelFormat}, " +
                    $"pixelFormat={pixelFormat}, pixelType={pixelType}, fullPush={fullPush}, hasPbo={pbo is not null}.");
            }

            // If a non-zero named buffer object is bound to the GL_PIXEL_UNPACK_BUFFER target (see glBindBuffer) while a texture image is specified,
            // the data ptr is treated as a byte offset into the buffer object's data store.
            if (!fullPush || StorageSet)
            {
                if (data is null)
                {
                    pbo?.Bind();
                    Api.TexSubImage2D(glTarget, i, 0, 0, w, h, pixelFormat, pixelType, null);
                    pbo?.Unbind();
                }
                else
                    Api.TexSubImage2D(glTarget, i, 0, 0, w, h, pixelFormat, pixelType, data.Address.Pointer);
            }
            else if (Data.MultiSample)
            {
                if (data is not null)
                    Debug.OpenGLWarning("Multisample textures do not support initial data, ignoring all mipmaps.");

                Api.TexImage2DMultisample(glTarget, Data.MultiSampleCount, internalPixelFormat, w, h, Data.FixedSampleLocations);
            }
            else if (data is not null)
                Api.TexImage2D(glTarget, i, internalPixelFormat, w, h, 0, pixelFormat, pixelType, data.Address.Pointer);
            else
            {
                pbo?.Bind();
                Api.TexImage2D(glTarget, i, internalPixelFormat, w, h, 0, pixelFormat, pixelType, null);
                pbo?.Unbind();
            }
        }

        private unsafe void PushWithDataRows(
            GLEnum glTarget,
            int i,
            uint w,
            uint h,
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
                i,
                w,
                h,
                internalPixelFormat,
                ref pixelFormat,
                ref pixelType);
            if (uploadParamsAdjusted && startRow == 0)
            {
                Debug.OpenGLWarning(
                    $"[GLTexture2D] Sanitized TexSubImage2D row upload parameters for '{GetDescribingName()}', mip={i}, " +
                    $"size={w}x{h}, rows={startRow}..{startRow + rowCount - 1}, sized={Data.SizedInternalFormat}, internal={internalPixelFormat}, " +
                    $"pixelFormat={originalPixelFormat}->{pixelFormat}, pixelType={originalPixelType}->{pixelType}.");
            }

            if (fullPush && !StorageSet)
            {
                Api.TexImage2D(glTarget, i, internalPixelFormat, w, h, 0, pixelFormat, pixelType, data.Address.Pointer);
                return;
            }

            long rowBytes = data.Length / (long)Math.Max(1u, h);
            byte* rowPointer = (byte*)data.Address.Pointer + (startRow * rowBytes);
            Debug.OpenGLWarning(
                $"[GLTexture2D] TexSubImage2D PRE '{GetDescribingName()}' mip={i} size={w}x{h} rows={startRow}+{rowCount} " +
                $"format={pixelFormat} type={pixelType} internal={internalPixelFormat} sized={Data.SizedInternalFormat} storage={StorageSet}");
            Api.TexSubImage2D(glTarget, i, 0, startRow, w, (uint)rowCount, pixelFormat, pixelType, rowPointer);
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

        private bool IsMipLevelInAllocatedRange(int glLevel)
            => glLevel >= 0 && glLevel < _allocatedLevels;

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

        protected override void SetParameters()
        {
            base.SetParameters();

            if (IsMultisampleTarget)
                return;
            
            Api.TextureParameter(BindingId, GLEnum.TextureLodBias, Data.LodBias);

            //int dsmode = Data.DepthStencilFormat == EDepthStencilFmt.Stencil ? (int)GLEnum.StencilIndex : (int)GLEnum.DepthComponent;
            //Api.TextureParameterI(BindingId, GLEnum.DepthStencilTextureMode, in dsmode);

            int magFilter = (int)ToGLEnum(Data.MagFilter);
            Api.TextureParameterI(BindingId, GLEnum.TextureMagFilter, in magFilter);

            int minFilter = (int)ToGLEnum(Data.MinFilter);
            Api.TextureParameterI(BindingId, GLEnum.TextureMinFilter, in minFilter);
            ApplyTextureAnisotropy();

            int uWrap = (int)ToGLEnum(Data.UWrap);
            Api.TextureParameterI(BindingId, GLEnum.TextureWrapS, in uWrap);

            int vWrap = (int)ToGLEnum(Data.VWrap);
            Api.TextureParameterI(BindingId, GLEnum.TextureWrapT, in vWrap);

            // Depth-comparison mode for hardware PCF (sampler2DShadow).
            int compareMode = (int)(Data.EnableComparison ? GLEnum.CompareRefToTexture : GLEnum.None);
            Api.TextureParameterI(BindingId, GLEnum.TextureCompareMode, in compareMode);
            if (Data.EnableComparison)
            {
                int compareFunc = (int)ToGLEnum(Data.CompareFunc);
                Api.TextureParameterI(BindingId, GLEnum.TextureCompareFunc, in compareFunc);
            }

            // Clamp base/max mip level to what we actually have.
            // This is critical for render-target textures (e.g., shadow maps) that only define mip 0.
            // Leaving maxLevel at a large default (e.g., 1000) can make the driver treat the attachment as incomplete.
            ApplyMipRangeParameters();

        }

        public override void PreSampling()
            => Data.GrabPass?.Grab(
                Data.GrabPass.ReadFBO ?? XRFrameBuffer.BoundForWriting,
                Engine.Rendering.State.RenderingPipelineState?.WindowViewport);

        protected internal override void PostGenerated()
        {
            static void SetFullPush(MipmapInfo m)
                => m.NeedsFullPush = true;
            Mipmaps.ForEach(SetFullPush);
            StorageSet = false;
            _sparseStorageAllocated = false;
            _sparseLogicalWidth = 0;
            _sparseLogicalHeight = 0;
            _sparseLogicalMipCount = 0;
            _sparseNumSparseLevels = 0;
            Data.ClearSparseTextureStreamingState();
            base.PostGenerated();
        }
        protected internal override void PostDeleted()
        {
            if (_externalMemoryObject != 0)
            {
                Renderer.DeleteMemoryObject(_externalMemoryObject);
                _externalMemoryObject = 0;
            }

            // Track VRAM deallocation
            if (_allocatedVRAMBytes > 0)
            {
                Engine.Rendering.Stats.RemoveTextureAllocation(_allocatedVRAMBytes);
                _allocatedVRAMBytes = 0;
            }
            StorageSet = false;
            _sparseStorageAllocated = false;
            _sparseLogicalWidth = 0;
            _sparseLogicalHeight = 0;
            _sparseLogicalMipCount = 0;
            _sparseNumSparseLevels = 0;
            Data.ClearSparseTextureStreamingState();
            base.PostDeleted();
        }

        /// <summary>
        /// Calculates the approximate VRAM size for a 2D texture including all mipmap levels.
        /// </summary>
        internal static long CalculateTextureVRAMSize(uint width, uint height, uint mipLevels, ESizedInternalFormat format, uint sampleCount)
        {
            long totalSize = 0;
            uint bpp = GetBytesPerPixel(format);
            
            for (uint mip = 0; mip < mipLevels; mip++)
            {
                uint mipWidth = Math.Max(1u, width >> (int)mip);
                uint mipHeight = Math.Max(1u, height >> (int)mip);
                totalSize += mipWidth * mipHeight * bpp * sampleCount;
            }
            
            return totalSize;
        }

        /// <summary>
        /// Returns the bytes per pixel for a given sized internal format.
        /// </summary>
        internal static uint GetBytesPerPixel(ESizedInternalFormat format)
        {
            return format switch
            {
                ESizedInternalFormat.R8 => 1,
                ESizedInternalFormat.R8Snorm => 1,
                ESizedInternalFormat.R16 => 2,
                ESizedInternalFormat.R16Snorm => 2,
                ESizedInternalFormat.Rg8 => 2,
                ESizedInternalFormat.Rg8Snorm => 2,
                ESizedInternalFormat.Rg16 => 4,
                ESizedInternalFormat.Rg16Snorm => 4,
                ESizedInternalFormat.Rgb8 => 3,
                ESizedInternalFormat.Rgb8Snorm => 3,
                ESizedInternalFormat.Rgb16Snorm => 6,
                ESizedInternalFormat.Rgba8 => 4,
                ESizedInternalFormat.Rgba8Snorm => 4,
                ESizedInternalFormat.Rgba16 => 8,
                ESizedInternalFormat.Srgb8 => 3,
                ESizedInternalFormat.Srgb8Alpha8 => 4,
                ESizedInternalFormat.R16f => 2,
                ESizedInternalFormat.Rg16f => 4,
                ESizedInternalFormat.Rgb16f => 6,
                ESizedInternalFormat.Rgba16f => 8,
                ESizedInternalFormat.R32f => 4,
                ESizedInternalFormat.Rg32f => 8,
                ESizedInternalFormat.Rgb32f => 12,
                ESizedInternalFormat.Rgba32f => 16,
                ESizedInternalFormat.R11fG11fB10f => 4,
                ESizedInternalFormat.Rgb9E5 => 4,
                ESizedInternalFormat.R8i => 1,
                ESizedInternalFormat.R8ui => 1,
                ESizedInternalFormat.R16i => 2,
                ESizedInternalFormat.R16ui => 2,
                ESizedInternalFormat.R32i => 4,
                ESizedInternalFormat.R32ui => 4,
                ESizedInternalFormat.Rg8i => 2,
                ESizedInternalFormat.Rg8ui => 2,
                ESizedInternalFormat.Rg16i => 4,
                ESizedInternalFormat.Rg16ui => 4,
                ESizedInternalFormat.Rg32i => 8,
                ESizedInternalFormat.Rg32ui => 8,
                ESizedInternalFormat.Rgb8i => 3,
                ESizedInternalFormat.Rgb8ui => 3,
                ESizedInternalFormat.Rgb16i => 6,
                ESizedInternalFormat.Rgb16ui => 6,
                ESizedInternalFormat.Rgb32i => 12,
                ESizedInternalFormat.Rgb32ui => 12,
                ESizedInternalFormat.Rgba8i => 4,
                ESizedInternalFormat.Rgba8ui => 4,
                ESizedInternalFormat.Rgba16i => 8,
                ESizedInternalFormat.Rgba16ui => 8,
                ESizedInternalFormat.Rgba32i => 16,
                ESizedInternalFormat.Rgba32ui => 16,
                ESizedInternalFormat.DepthComponent16 => 2,
                ESizedInternalFormat.DepthComponent24 => 3,
                ESizedInternalFormat.DepthComponent32f => 4,
                ESizedInternalFormat.Depth24Stencil8 => 4,
                ESizedInternalFormat.Depth32fStencil8 => 5,
                ESizedInternalFormat.StencilIndex8 => 1,
                _ => 4, // Default assumption for unknown/other formats
            };
        }
    }
}
