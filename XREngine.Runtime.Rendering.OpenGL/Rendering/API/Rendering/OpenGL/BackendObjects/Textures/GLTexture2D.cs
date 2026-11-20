using Silk.NET.OpenGL;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Extensions;

namespace XREngine.Rendering.OpenGL;

public partial class GLTexture2D(OpenGLRenderer renderer, XRTexture2D data) : GLTexture<XRTexture2D>(renderer, data)
{
    private MipmapInfo[] _mipmaps = [];
    // GL-side per-mip metadata cache rebuilt from Data.Mipmaps. Reassigning this array
    // is not a GL-state change in itself; do not invalidate the GL handle when it updates.
    [TransientGLState]
    public MipmapInfo[] Mipmaps
    {
        get => _mipmaps;
        private set => SetField(ref _mipmaps, value);
    }

    private bool _storageSet = false;
    // Internal bookkeeping that records whether glTextureStorage2D has run for the current
    // immutable-storage allocation. EnsureStorageAllocated flips this true right after the
    // storage call; without [TransientGLState] that flip would invalidate the GL handle and
    // force the next bind to Destroy/Generate, wiping the freshly allocated storage.
    [TransientGLState]
    public bool StorageSet
    {
        get => _storageSet;
        private set => SetField(ref _storageSet, value);
    }

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
        // creates fresh storage. Without this, TexSubImage2D would receive
        // width/height that exceed the allocated storage and produce GL_INVALID_VALUE,
        // or mip levels beyond the allocated count would fail with
        // "Invalid texture format".
        if (StorageSet && sourceMipmaps.Length > 0)
        {
            uint newWidth = sourceMipmaps[0].Width;
            uint newHeight = sourceMipmaps[0].Height;
            uint requiredLevels = (uint)sourceMipmaps.Length;
            bool switchingFromSparseStorage = _sparseStorageAllocated && !Data.SparseTextureStreamingEnabled;
            if (Data.SparseTextureStreamingEnabled)
            {
                if (Data.SparseTextureStreamingLogicalWidth > 0)
                    newWidth = Data.SparseTextureStreamingLogicalWidth;
                if (Data.SparseTextureStreamingLogicalHeight > 0)
                    newHeight = Data.SparseTextureStreamingLogicalHeight;

                requiredLevels = Data.SparseTextureStreamingLogicalMipCount > 0
                    ? (uint)Data.SparseTextureStreamingLogicalMipCount
                    : (uint)(SparseTextureResidentBaseMipLevelOrZero + sourceMipmaps.Length);
            }

            if (switchingFromSparseStorage
                || newWidth != _allocatedWidth
                || newHeight != _allocatedHeight
                || requiredLevels > _allocatedLevels)
            {
                DataResized();
            }
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

    private int SparseTextureResidentBaseMipLevelOrZero
    {
        get
        {
            int baseMipLevel = Data.SparseTextureStreamingResidentBaseMipLevel;
            return baseMipLevel == int.MaxValue ? 0 : Math.Max(0, baseMipLevel);
        }
    }

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
        // No-op when the resize event fires with the same dimensions/format we already hold
        // immutable storage for. This was observed in the GL submit trace as a per-frame
        // destroy/recreate/storage/upload cycle on font glyph and similar runtime textures
        // (~30+ Hz), causing severe lag without changing any GL state. Without this guard the
        // code below would tear down a valid immutable texture and rebuild an identical one
        // every time. Sparse and external-memory textures take the slow path so their
        // promotion logic still runs.
        if (!Data.Resizable
            && StorageSet
            && !_pendingImmutableStorageRecreate
            && !Data.SparseTextureStreamingEnabled
            && !Data.UsesOpenGlExternalMemoryImport
            && _externalMemoryObject == 0
            && _allocatedWidth == Math.Max(1u, Data.Width)
            && _allocatedHeight == Math.Max(1u, Data.Height)
            && _allocatedInternalFormat == Data.SizedInternalFormat)
        {
            return;
        }

        bool requiresImmutableRecreate = !Data.Resizable && (StorageSet || _pendingImmutableStorageRecreate);
        uint previousLevels = _allocatedLevels;
        uint previousW = _allocatedWidth;
        uint previousH = _allocatedHeight;
        StorageSet = false;
        _allocatedLevels = 0;
        _allocatedWidth = 0;
        _allocatedHeight = 0;
        _allocatedInternalFormat = default;
        AdvanceStorageGeneration();
        _sparseStorageAllocated = false;
        _sparseLogicalWidth = 0;
        _sparseLogicalHeight = 0;
        _sparseLogicalMipCount = 0;
        _sparseNumSparseLevels = 0;
        ClearProgressiveVisibleMipRange();
        Data.ClearSparseTextureStreamingState();
        Mipmaps.ForEach(m =>
        {
            m.NeedsFullPush = true;
            //m.HasPushedUpdateData = false;
        });

        // Immutable storage (glTextureStorage2D) cannot be re-allocated on the same
        // GL name, so the name must be destroyed and generated again. The destroy runs
        // now only on the render thread and only while no Advanced bindless lease or
        // in-flight native work holds the current storage. Otherwise the flag stays set,
        // and the next upload entry point applies the recreate before it uploads.
        if (requiresImmutableRecreate)
        {
            Debug.OpenGL(
                $"[GLTexture2D] DataResized scheduling immutable recreate for '{GetDescribingName()}': binding={BindingId} " +
                $"previousDims={previousW}x{previousH} previousLevels={previousLevels} " +
                $"newDims={Data.Width}x{Data.Height} newMipmapCount={Mipmaps?.Length ?? 0} " +
                $"onRenderThread={RuntimeEngine.IsRenderThread}.");
            _pendingImmutableStorageRecreate = true;
            if (!TryApplyPendingImmutableStorageRecreate())
                Invalidate();
        }
        else
        {
            Invalidate();
        }
    }

    /// <summary>
    /// Replaces the immutable storage when the texture data changed its size, so the next upload
    /// fills storage with the new dimensions. Returns true when the storage matches the data (no
    /// recreate was pending, or it ran now). Returns false while the recreate must wait: off the
    /// render thread, while an Advanced bindless sampler-pair lease or in-flight native work
    /// still references the current storage. The flag then stays set, and the caller must not
    /// upload: the data no longer fits this storage (for example, small block-compressed levels
    /// would not reach the edge of the old, smaller levels).
    /// </summary>
    private bool TryApplyPendingImmutableStorageRecreate()
    {
        if (!_pendingImmutableStorageRecreate)
            return true;
        if (!RuntimeEngine.IsRenderThread || HasActiveAdvancedBindlessPairLeases || HasInFlightNativeOperation)
            return false;

        _pendingImmutableStorageRecreate = false;
        if (IsGenerated)
        {
            if (GLSubmitTracer.Enabled)
                TracePreSubmit("Destroy.ApplyPending", _allocatedLevels, _allocatedWidth, _allocatedHeight);
            Destroy();
        }
        return true;
    }

    /// <summary>
    /// Commits immutable GL storage before this texture is attached to an FBO. Without
    /// this, NVIDIA's driver fastfails inside glNamedFramebufferTexture on textures whose
    /// glTextureStorage2D call has not yet run (see EnsureStorageAllocated for details).
    /// Mutable (Resizable=true) textures are intentionally left to PushData; trying to
    /// pre-commit mutable storage here was observed to throw on certain sized formats and
    /// to leave the GL state in a worse spot than skipping the attach.
    /// </summary>
    protected override void EnsureStorageAllocatedForFBOAttach()
    {
        // A recreate that must wait keeps the old storage on the GL name. Do not allocate
        // storage again on that immutable name; the attach uses the old storage.
        if (!TryApplyPendingImmutableStorageRecreate())
            return;

        if (!Data.Resizable)
            EnsureStorageAllocated();
    }

    public override void PreSampling()
        => Data.GrabPass?.Grab(
            Data.GrabPass.ReadFBO ?? XRFrameBuffer.BoundForWriting,
            RuntimeEngine.Rendering.State.RenderingPipelineState?.WindowViewport);

    protected internal override void PostGenerated()
    {
        static void SetFullPush(MipmapInfo mipmap)
            => mipmap.NeedsFullPush = true;

        Mipmaps.ForEach(SetFullPush);
        StorageSet = false;
        _allocatedLevels = 0;
        _allocatedWidth = 0;
        _allocatedHeight = 0;
        _allocatedInternalFormat = default;
        AdvanceStorageGeneration();
        _sparseStorageAllocated = false;
        _sparseLogicalWidth = 0;
        _sparseLogicalHeight = 0;
        _sparseLogicalMipCount = 0;
        _sparseNumSparseLevels = 0;
        Data.RetireSparseTextureStreamingStorage();
        base.PostGenerated();
    }

    protected internal override void PostDeleted()
    {
        if (_externalMemoryObject != 0)
        {
            Renderer.DeleteMemoryObject(_externalMemoryObject);
            _externalMemoryObject = 0;
        }

        // Track VRAM deallocation.
        if (_allocatedVRAMBytes > 0)
        {
            RuntimeEngine.Rendering.Stats.Vram.RemoveTextureAllocation(_allocatedVRAMBytes);
            _allocatedVRAMBytes = 0;
        }

        StorageSet = false;
        _allocatedLevels = 0;
        _allocatedWidth = 0;
        _allocatedHeight = 0;
        _allocatedInternalFormat = default;
        AdvanceStorageGeneration();
        _sparseStorageAllocated = false;
        _sparseLogicalWidth = 0;
        _sparseLogicalHeight = 0;
        _sparseLogicalMipCount = 0;
        _sparseNumSparseLevels = 0;
        Data.RetireSparseTextureStreamingStorage();
        base.PostDeleted();
    }
}
