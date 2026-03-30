using Extensions;
using Silk.NET.OpenGL;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public class GLTexture2DArray(OpenGLRenderer renderer, XRTexture2DArray data) : GLTexture<XRTexture2DArray>(renderer, data)
    {
        private long _allocatedVRAMBytes = 0;
        private bool _storageSet = false;
        private ESizedInternalFormat _allocatedInternalFormat = ESizedInternalFormat.Rgba8;
        private uint _allocatedWidth = 0;
        private uint _allocatedHeight = 0;
        private uint _allocatedDepth = 0;
        private uint _allocatedLevels = 0;

        public class MipmapInfo : XRBase
        {
            private bool _hasPushedResizedData = false;
            //private bool _hasPushedUpdateData = false;
            private readonly Mipmap2D _mipmap;
            private readonly GLTexture2DArray _textureArray;

            public Mipmap2D Mipmap => _mipmap;

            public MipmapInfo(GLTexture2DArray textureArray, Mipmap2D mipmap)
            {
                _textureArray = textureArray;
                _mipmap = mipmap;
                _mipmap.PropertyChanged += MipmapPropertyChanged;
                _mipmap.Invalidated += MipmapInvalidated;
            }

            ~MipmapInfo()
            {
                _mipmap.PropertyChanged -= MipmapPropertyChanged;
                _mipmap.Invalidated -= MipmapInvalidated;
            }

            private void MipmapInvalidated()
            {
                _textureArray.Invalidate();
            }

            private void MipmapPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                switch (e.PropertyName)
                {
                    case nameof(Mipmap.Data):
                        //HasPushedUpdateData = false;
                        _textureArray.Invalidate();
                        break;
                    case nameof(Mipmap.Width):
                    case nameof(Mipmap.Height):
                    case nameof(Mipmap.InternalFormat):
                    case nameof(Mipmap.PixelFormat):
                    case nameof(Mipmap.PixelType):
                        HasPushedResizedData = false;
                        //HasPushedUpdateData = false;
                        _textureArray.Invalidate();
                        break;
                }
            }

            public bool HasPushedResizedData
            {
                get => _hasPushedResizedData;
                set => SetField(ref _hasPushedResizedData, value);
            }
            //public bool HasPushedUpdateData
            //{
            //    get => _hasPushedUpdateData;
            //    set => SetField(ref _hasPushedUpdateData, value);
            //}
        }

        public MipmapInfo[] Mipmaps { get; private set; } = [];

        protected override void DataPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.DataPropertyChanged(sender, e);
            switch (e.PropertyName)
            {
                case nameof(XRTexture2DArray.Mipmaps):
                    {
                        UpdateMipmaps();
                        break;
                    }
            }
        }

        private void UpdateMipmaps()
        {
            if (Data.Mipmaps is null || Data.Mipmaps.Length == 0)
            {
                Mipmaps = [];
                Invalidate();
                return;
            }

            Mipmaps = new MipmapInfo[Data.Mipmaps.Length];
            for (int i = 0; i < Data.Mipmaps.Length; ++i)
                Mipmaps[i] = new MipmapInfo(this, Data.Mipmaps[i]);

            Invalidate();
        }

        public override ETextureTarget TextureTarget 
            => Data.MultiSample 
            ? ETextureTarget.Texture2DMultisampleArray 
            : ETextureTarget.Texture2DArray;

        protected override void UnlinkData()
        {
            base.UnlinkData();

            Data.AttachToFBORequested_OVRMultiView -= AttachToFBO_OVRMultiView;
            Data.DetachFromFBORequested_OVRMultiView -= DetachFromFBO_OVRMultiView;
            Data.PushDataRequested -= PushData;
            Data.BindRequested -= Bind;
            Data.UnbindRequested -= Unbind;
            Data.AttachImageToFBORequested -= AttachImageToFBO;
            Data.DetachImageFromFBORequested -= DetachImageFromFBO;
            Data.Resized -= DataResized;
            Mipmaps = [];
        }
        protected override void LinkData()
        {
            base.LinkData();

            Data.AttachToFBORequested_OVRMultiView += AttachToFBO_OVRMultiView;
            Data.DetachFromFBORequested_OVRMultiView += DetachFromFBO_OVRMultiView;
            Data.PushDataRequested += PushData;
            Data.AttachImageToFBORequested += AttachImageToFBO;
            Data.DetachImageFromFBORequested += DetachImageFromFBO;
            Data.BindRequested += Bind;
            Data.UnbindRequested += Unbind;
            Data.Resized += DataResized;
            UpdateMipmaps();
        }

        public override void AttachToFBO(XRFrameBuffer fbo, EFrameBufferAttachment attachment, int mipLevel = 0)
        {
            for (int i = 0; i < Data.Depth; ++i)
                AttachImageToFBO(fbo, attachment, i, mipLevel);
        }
        public override void DetachFromFBO(XRFrameBuffer fbo, EFrameBufferAttachment attachment, int mipLevel = 0)
        {
            for (int i = 0; i < Data.Depth; ++i)
                DetachImageFromFBO(fbo, attachment, i, mipLevel);
        }

        private void DetachImageFromFBO(XRFrameBuffer target, EFrameBufferAttachment attachment, int layer, int mipLevel)
        {
            if (Renderer.GetOrCreateAPIRenderObject(target) is not GLObjectBase apiFBO)
                return;

            Api.NamedFramebufferTextureLayer(apiFBO.BindingId, ToGLEnum(attachment), 0, mipLevel, layer);
            //Api.FramebufferTexture2D(GLEnum.Framebuffer, ToGLEnum(attachment), GLEnum.TextureCubeMapPositiveX + layer, BindingId, mipLevel);
        }

        private void AttachImageToFBO(XRFrameBuffer target, EFrameBufferAttachment attachment, int layer, int mipLevel)
        {
            if (Renderer.GetOrCreateAPIRenderObject(target) is not GLObjectBase apiFBO)
                return;

            Api.NamedFramebufferTextureLayer(apiFBO.BindingId, ToGLEnum(attachment), BindingId, mipLevel, layer);
            //Api.FramebufferTexture2D(GLEnum.Framebuffer, ToGLEnum(attachment), GLEnum.TextureCubeMapPositiveX + layer, BindingId, mipLevel);
        }

        private void DataResized()
        {
            _storageSet = false;
            _allocatedInternalFormat = ESizedInternalFormat.Rgba8;
            _allocatedWidth = 0;
            _allocatedHeight = 0;
            _allocatedDepth = 0;
            _allocatedLevels = 0;
            Mipmaps.ForEach(m =>
            {
                m.HasPushedResizedData = false;
                //m.HasPushedUpdateData = false;
            });
            Invalidate();
        }

        protected internal override void PostGenerated()
        {
            Mipmaps.ForEach(m =>
            {
                m.HasPushedResizedData = false;
                //m.HasPushedUpdateData = false;
            });
            _storageSet = false;
            _allocatedInternalFormat = ESizedInternalFormat.Rgba8;
            _allocatedWidth = 0;
            _allocatedHeight = 0;
            _allocatedDepth = 0;
            _allocatedLevels = 0;
            base.PostGenerated();
        }
        protected internal override void PostDeleted()
        {
            if (_allocatedVRAMBytes > 0)
            {
                Engine.Rendering.Stats.RemoveTextureAllocation(_allocatedVRAMBytes);
                _allocatedVRAMBytes = 0;
            }

            _storageSet = false;
            _allocatedInternalFormat = ESizedInternalFormat.Rgba8;
            _allocatedWidth = 0;
            _allocatedHeight = 0;
            _allocatedDepth = 0;
            _allocatedLevels = 0;
            base.PostDeleted();
        }

        private void EnsureStorage(ESizedInternalFormat desiredFormat, uint width, uint height, uint depth, uint levels)
        {
            bool needsAllocation = !_storageSet
                || _allocatedInternalFormat != desiredFormat
                || _allocatedWidth != width
                || _allocatedHeight != height
                || _allocatedDepth != depth
                || _allocatedLevels != levels;
            if (!needsAllocation)
                return;

            long requestedBytes = CalculateTextureArrayVRAMSize(width, height, depth, levels, desiredFormat);
            if (!Engine.Rendering.Stats.CanAllocateVram(requestedBytes, _allocatedVRAMBytes, out long projectedBytes, out long budgetBytes))
            {
                Debug.OpenGLWarning($"[VRAM Budget] Skipping 2D array texture allocation for '{Data.Name ?? BindingId.ToString()}' ({requestedBytes} bytes). Projected={projectedBytes} bytes, Budget={budgetBytes} bytes.");
                return;
            }

            if (_allocatedVRAMBytes > 0)
            {
                Engine.Rendering.Stats.RemoveTextureAllocation(_allocatedVRAMBytes);
                _allocatedVRAMBytes = 0;
            }

            Api.TextureStorage3D(BindingId, levels, ToGLEnum(desiredFormat), width, height, depth);
            _storageSet = true;
            _allocatedInternalFormat = desiredFormat;
            _allocatedWidth = width;
            _allocatedHeight = height;
            _allocatedDepth = depth;
            _allocatedLevels = levels;
            _allocatedVRAMBytes = requestedBytes;
            Engine.Rendering.Stats.AddTextureAllocation(_allocatedVRAMBytes);
        }

        private static long CalculateTextureArrayVRAMSize(uint width, uint height, uint depth, uint mipLevels, ESizedInternalFormat format)
        {
            long totalSize = 0;
            uint bpp = GLTexture2D.GetBytesPerPixel(format);

            for (uint mip = 0; mip < mipLevels; mip++)
            {
                uint mipWidth = Math.Max(1u, width >> (int)mip);
                uint mipHeight = Math.Max(1u, height >> (int)mip);
                totalSize += mipWidth * mipHeight * depth * bpp;
            }

            return totalSize;
        }

        private void EnsureFramebufferStorage()
        {
            uint width = Math.Max(1u, Data.Width);
            uint height = Math.Max(1u, Data.Height);
            uint depth = Math.Max(1u, Data.Depth);
            uint levels = (uint)Math.Max(1, Data.SmallestMipmapLevel + 1);
            EnsureStorage(Data.SizedInternalFormat, width, height, depth, levels);
        }

        private uint ResolveTargetLevels(XRTexture2D? firstSource, uint width, uint height)
        {
            if (firstSource is not null)
                return (uint)Math.Max(1, firstSource.SmallestMipmapLevel + 1);

            if (Data.AutoGenerateMipmaps)
                return (uint)Math.Max(1, XRTexture.GetSmallestMipmapLevel(width, height, Data.SmallestAllowedMipmapLevel) + 1);

            if (Data.SmallestAllowedMipmapLevel < 1000)
                return (uint)Math.Max(1, Data.SmallestMipmapLevel + 1);

            return 1;
        }

        private int ResolveMaxMipLevel(int baseLevel)
        {
            if (Data.MultiSample)
                return baseLevel;

            int configuredMaxLevel = Math.Max(baseLevel, Data.SmallestAllowedMipmapLevel);
            int allocatedMaxLevel = _allocatedLevels > 0
                ? Math.Max(baseLevel, (int)_allocatedLevels - 1)
                : baseLevel;

            return Math.Max(baseLevel, Math.Min(allocatedMaxLevel, configuredMaxLevel));
        }

        private void ApplyMipRangeParameters()
        {
            int baseLevel = Math.Max(0, Data.LargestMipmapLevel);
            int maxLevel = ResolveMaxMipLevel(baseLevel);

            Api.TextureParameterI(BindingId, GLEnum.TextureBaseLevel, in baseLevel);
            Api.TextureParameterI(BindingId, GLEnum.TextureMaxLevel, in maxLevel);
        }

        public override void PushData()
        {
            if (IsPushing)
                return;
            try
            {
                IsPushing = true;

                OnPrePushData(out bool shouldPush, out bool allowPostPushCallback);
                if (!shouldPush)
                {
                    if (allowPostPushCallback)
                        OnPostPushData();
                    IsPushing = false;
                    return;
                }

                Bind();

                Api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

                // Fast path: render-target arrays already have GPU storage populated via FBO. Skip CPU->GPU copy from slices.
                if (Data.FrameBufferAttachment.HasValue)
                {
                    EnsureFramebufferStorage();

                    // Ensure sampler state is up to date and bail out.
                    int magFilterRt = (int)ToGLEnum(Data.MagFilter);
                    Api.TextureParameterI(BindingId, GLEnum.TextureMagFilter, in magFilterRt);

                    int minFilterRt = (int)ToGLEnum(Data.MinFilter);
                    Api.TextureParameterI(BindingId, GLEnum.TextureMinFilter, in minFilterRt);

                    int uWrapRt = (int)ToGLEnum(Data.UWrap);
                    Api.TextureParameterI(BindingId, GLEnum.TextureWrapS, in uWrapRt);

                    int vWrapRt = (int)ToGLEnum(Data.VWrap);
                    Api.TextureParameterI(BindingId, GLEnum.TextureWrapT, in vWrapRt);

                    // Depth-comparison mode for hardware PCF (sampler2DArrayShadow).
                    int compareModeRt = (int)(Data.EnableComparison ? GLEnum.CompareRefToTexture : GLEnum.None);
                    Api.TextureParameterI(BindingId, GLEnum.TextureCompareMode, in compareModeRt);
                    if (Data.EnableComparison)
                    {
                        int compareFuncRt = (int)ToGLEnum(Data.CompareFunc);
                        Api.TextureParameterI(BindingId, GLEnum.TextureCompareFunc, in compareFuncRt);
                    }

                    ApplyMipRangeParameters();

                    if (allowPostPushCallback)
                        OnPostPushData();
                    IsPushing = false;
                    return;
                }

                // Choose a sized internal format that matches the first available source texture so CopyImageSubData is compatible.
                XRTexture2D? firstSource = null;
                for (int i = 0; i < Data.Textures.Length && firstSource is null; ++i)
                    firstSource = Data.Textures[i];

                var desiredInternalFormat = Data.SizedInternalFormat;
                if (firstSource is not null && desiredInternalFormat != firstSource.SizedInternalFormat)
                {
                    Debug.OpenGL($"Adjusting texture array '{Data.Name}' internal format from {desiredInternalFormat} to {firstSource.SizedInternalFormat} to match source textures.");
                    desiredInternalFormat = firstSource.SizedInternalFormat;
                    Data.SizedInternalFormat = desiredInternalFormat;
                }

                // Ensure array dimensions match the first valid source so storage is allocated correctly.
                uint targetWidth = Data.Width;
                uint targetHeight = Data.Height;
                uint targetDepth = Math.Max(1u, Data.Depth > 0 ? Data.Depth : (uint)Data.Textures.Length);

                if (targetWidth == 0 || targetHeight == 0)
                {
                    if (firstSource is not null)
                    {
                        targetWidth = firstSource.Width;
                        targetHeight = firstSource.Height;
                    }
                    else
                    {
                        targetWidth = Math.Max(1u, targetWidth);
                        targetHeight = Math.Max(1u, targetHeight);
                    }
                }
                else
                {
                    targetWidth = Math.Max(1u, targetWidth);
                    targetHeight = Math.Max(1u, targetHeight);
                }

                uint targetLevels = ResolveTargetLevels(firstSource, targetWidth, targetHeight);

                // Allocate storage (or reallocate if format changed) before copying data.
                EnsureStorage(desiredInternalFormat, targetWidth, targetHeight, targetDepth, targetLevels);

                if (firstSource is null)
                {
                    int minLODNoSource = -1000;
                    int maxLODNoSource = 1000;
                    Api.TextureParameterI(BindingId, GLEnum.TextureMinLod, in minLODNoSource);
                    Api.TextureParameterI(BindingId, GLEnum.TextureMaxLod, in maxLODNoSource);

                    int magFilterNoSource = (int)ToGLEnum(Data.MagFilter);
                    Api.TextureParameterI(BindingId, GLEnum.TextureMagFilter, in magFilterNoSource);

                    int minFilterNoSource = (int)ToGLEnum(Data.MinFilter);
                    Api.TextureParameterI(BindingId, GLEnum.TextureMinFilter, in minFilterNoSource);

                    int uWrapNoSource = (int)ToGLEnum(Data.UWrap);
                    Api.TextureParameterI(BindingId, GLEnum.TextureWrapS, in uWrapNoSource);

                    int vWrapNoSource = (int)ToGLEnum(Data.VWrap);
                    Api.TextureParameterI(BindingId, GLEnum.TextureWrapT, in vWrapNoSource);

                    int compareModeNoSource = (int)(Data.EnableComparison ? GLEnum.CompareRefToTexture : GLEnum.None);
                    Api.TextureParameterI(BindingId, GLEnum.TextureCompareMode, in compareModeNoSource);
                    if (Data.EnableComparison)
                    {
                        int compareFuncNoSource = (int)ToGLEnum(Data.CompareFunc);
                        Api.TextureParameterI(BindingId, GLEnum.TextureCompareFunc, in compareFuncNoSource);
                    }

                    ApplyMipRangeParameters();

                    if (allowPostPushCallback)
                        OnPostPushData();
                    IsPushing = false;
                    return;
                }
                
                // Copy each source texture's data to its respective layer in the array
                for (int layer = 0; layer < Data.Textures.Length; ++layer)
                {
                    var tex = Data.Textures[layer];
                    if (tex is null)
                        continue;

                    // Get the GL object for the source texture so we can copy from GPU to GPU
                    var glSourceTex = Renderer.GetOrCreateAPIRenderObject(tex) as GLTexture2D;
                    if (glSourceTex is null)
                        continue;

                    // Make sure source texture is valid on the GPU
                    glSourceTex.Bind();
                    glSourceTex.Unbind();

                    uint srcId = glSourceTex.BindingId;

                    if (!Api.IsTexture(srcId))
                    {
                        Debug.OpenGLWarning($"Skipping copy into texture array layer {layer} because source texture id {srcId} is not valid.");
                        continue;
                    }

                    if (!Api.IsTexture(BindingId))
                    {
                        Debug.OpenGLWarning($"Skipping copy into texture array because destination texture id {BindingId} is not valid.");
                        break;
                    }

                    if (tex.SizedInternalFormat != Data.SizedInternalFormat)
                    {
                        Debug.OpenGLWarning($"Skipping copy into texture array layer {layer} because source internal format {tex.SizedInternalFormat} != target {Data.SizedInternalFormat}.");
                        continue;
                    }

                    if (tex.Width != targetWidth || tex.Height != targetHeight)
                    {
                        Debug.OpenGLWarning($"Skipping copy into texture array layer {layer} because source size {tex.Width}x{tex.Height} != target {targetWidth}x{targetHeight}.");
                        continue;
                    }

                    if (srcId == InvalidBindingId)
                        continue;

                    // Copy all mip levels from the source 2D texture to this layer of the array
                    int numMips = Math.Max(1, tex.SmallestMipmapLevel + 1);
                    numMips = Math.Min(numMips, (int)targetLevels);
                    for (int mip = 0; mip < numMips; ++mip)
                    {
                        Api.GetTextureLevelParameter(srcId, mip, GLEnum.TextureWidth, out int sourceMipWidth);
                        Api.GetTextureLevelParameter(srcId, mip, GLEnum.TextureHeight, out int sourceMipHeight);

                        if (sourceMipWidth <= 0 || sourceMipHeight <= 0)
                            break;

                        uint mipWidth = (uint)sourceMipWidth;
                        uint mipHeight = (uint)sourceMipHeight;

                        // glCopyImageSubData(srcName, srcTarget, srcLevel, srcX, srcY, srcZ,
                        //                    dstName, dstTarget, dstLevel, dstX, dstY, dstZ,
                        //                    srcWidth, srcHeight, srcDepth)
                        Api.CopyImageSubData(
                            srcId, CopyImageSubDataTarget.Texture2D, mip, 0, 0, 0,
                            BindingId, CopyImageSubDataTarget.Texture2DArray, mip, 0, 0, layer,
                            mipWidth, mipHeight, 1);
                    }
                }

                int minLOD = -1000;
                int maxLOD = 1000;

                Api.TextureParameterI(BindingId, GLEnum.TextureMinLod, in minLOD);
                Api.TextureParameterI(BindingId, GLEnum.TextureMaxLod, in maxLOD);

                // Set filter and wrap parameters (required for correct sampling)
                int magFilter = (int)ToGLEnum(Data.MagFilter);
                Api.TextureParameterI(BindingId, GLEnum.TextureMagFilter, in magFilter);

                int minFilter = (int)ToGLEnum(Data.MinFilter);
                Api.TextureParameterI(BindingId, GLEnum.TextureMinFilter, in minFilter);

                int uWrap = (int)ToGLEnum(Data.UWrap);
                Api.TextureParameterI(BindingId, GLEnum.TextureWrapS, in uWrap);

                int vWrap = (int)ToGLEnum(Data.VWrap);
                Api.TextureParameterI(BindingId, GLEnum.TextureWrapT, in vWrap);

                // Depth-comparison mode for hardware PCF (sampler2DArrayShadow).
                int compareMode = (int)(Data.EnableComparison ? GLEnum.CompareRefToTexture : GLEnum.None);
                Api.TextureParameterI(BindingId, GLEnum.TextureCompareMode, in compareMode);
                if (Data.EnableComparison)
                {
                    int compareFunc = (int)ToGLEnum(Data.CompareFunc);
                    Api.TextureParameterI(BindingId, GLEnum.TextureCompareFunc, in compareFunc);
                }

                ApplyMipRangeParameters();

                if (Data.AutoGenerateMipmaps)
                    GenerateMipmaps();

                if (allowPostPushCallback)
                    OnPostPushData();
            }
            catch (Exception ex)
            {
                Debug.OpenGLException(ex);
            }
            finally
            {
                IsPushing = false;
                Unbind();
            }
        }
    }
}