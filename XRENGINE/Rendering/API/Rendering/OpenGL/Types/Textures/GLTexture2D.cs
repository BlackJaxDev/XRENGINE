using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Extensions;

namespace XREngine.Rendering.OpenGL;

public partial class GLTexture2D(OpenGLRenderer renderer, XRTexture2D data) : GLTexture<XRTexture2D>(renderer, data)
{
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

            if (newWidth != _allocatedWidth
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
        bool requiresImmutableRecreate = !Data.Resizable && (StorageSet || _pendingImmutableStorageRecreate);
        uint previousLevels = _allocatedLevels;
        uint previousW = _allocatedWidth;
        uint previousH = _allocatedHeight;
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
        ClearProgressiveVisibleMipRange();
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
            Debug.OpenGL(
                $"[GLTexture2D] DataResized scheduling immutable recreate for '{GetDescribingName()}': binding={BindingId} " +
                $"previousDims={previousW}x{previousH} previousLevels={previousLevels} " +
                $"newDims={Data.Width}x{Data.Height} newMipmapCount={Mipmaps?.Length ?? 0} " +
                $"onRenderThread={Engine.IsRenderThread}.");
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
        {
            Invalidate();
        }
    }

    private void ApplyPendingImmutableStorageRecreate()
    {
        if (!_pendingImmutableStorageRecreate || !Engine.IsRenderThread)
            return;

        _pendingImmutableStorageRecreate = false;
        if (IsGenerated)
            Destroy();
    }

    public override void PreSampling()
        => Data.GrabPass?.Grab(
            Data.GrabPass.ReadFBO ?? XRFrameBuffer.BoundForWriting,
            Engine.Rendering.State.RenderingPipelineState?.WindowViewport);

    protected internal override void PostGenerated()
    {
        static void SetFullPush(MipmapInfo mipmap)
            => mipmap.NeedsFullPush = true;

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

        // Track VRAM deallocation.
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
}
