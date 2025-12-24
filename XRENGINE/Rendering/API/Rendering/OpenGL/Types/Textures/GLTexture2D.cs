using Extensions;
using Silk.NET.OpenGL;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
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

        /// <summary>
        /// Tracks the currently allocated GPU memory size for this texture in bytes.
        /// </summary>
        private long _allocatedVRAMBytes = 0;

        protected override void DataPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.DataPropertyChanged(sender, e);
            switch (e.PropertyName)
            {
                case nameof(XRTexture2D.Mipmaps):
                    {
                        UpdateMipmaps();
                        break;
                    }
            }
        }

        private void UpdateMipmaps()
        {
            Mipmaps = new MipmapInfo[Data.Mipmaps.Length];
            for (int i = 0; i < Data.Mipmaps.Length; ++i)
                Mipmaps[i] = new MipmapInfo(this, Data.Mipmaps[i]);
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
            Mipmaps = [];
        }
        protected override void LinkData()
        {
            base.LinkData();

            Data.Resized += DataResized;
            UpdateMipmaps();
        }

        private void DataResized()
        {
            StorageSet = false;
            Mipmaps.ForEach(m =>
            {
                m.NeedsFullPush = true;
                //m.HasPushedUpdateData = false;
            });
            Invalidate();
        }

        public override unsafe void PushData()
        {
            if (IsPushing)
                return;
            try
            {
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

                Bind();

                var glTarget = ToGLEnum(TextureTarget);

                Api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

                EPixelInternalFormat? internalFormatForce = null;
                if (!Data.Resizable && !StorageSet)
                {
                    // Track VRAM deallocation of previous texture if any
                    if (_allocatedVRAMBytes > 0)
                    {
                        Engine.Rendering.Stats.RemoveTextureAllocation(_allocatedVRAMBytes);
                        _allocatedVRAMBytes = 0;
                    }

                    // Guard against 0-sized textures which cause GL_INVALID_VALUE
                    uint w = Math.Max(1u, Data.Width);
                    uint h = Math.Max(1u, Data.Height);
                    // SmallestMipmapLevel is the smallest mip *index* (0..N), so storage needs N+1 levels.
                    uint levels = (uint)Math.Max(1, Data.SmallestMipmapLevel + 1);
                    
                    if (Data.MultiSample)
                        Api.TextureStorage2DMultisample(BindingId, Data.MultiSampleCount, ToGLEnum(Data.SizedInternalFormat), w, h, Data.FixedSampleLocations);
                    else
                        Api.TextureStorage2D(BindingId, levels, ToGLEnum(Data.SizedInternalFormat), w, h);
                    internalFormatForce = ToBaseInternalFormat(Data.SizedInternalFormat);
                    StorageSet = true;

                    // Track VRAM allocation - calculate size with mipmaps
                    _allocatedVRAMBytes = CalculateTextureVRAMSize(w, h, levels, Data.SizedInternalFormat, Data.MultiSample ? Data.MultiSampleCount : 1u);
                    Engine.Rendering.Stats.AddTextureAllocation(_allocatedVRAMBytes);
                }

                if (Mipmaps is null || Mipmaps.Length == 0)
                    PushMipmap(glTarget, 0, null, internalFormatForce);
                else
                {
                    for (int i = 0; i < Mipmaps.Length; ++i)
                        PushMipmap(glTarget, i, Mipmaps[i], internalFormatForce);
                }

                int baseLevel = 0;
                int maxLevel = 1000;
                int minLOD = -1000;
                int maxLOD = 1000;

                Api.TextureParameterI(BindingId, GLEnum.TextureBaseLevel, in baseLevel);
                Api.TextureParameterI(BindingId, GLEnum.TextureMaxLevel, in maxLevel);

                if (!IsMultisampleTarget)
                {
                    Api.TextureParameterI(BindingId, GLEnum.TextureMinLod, in minLOD);
                    Api.TextureParameterI(BindingId, GLEnum.TextureMaxLod, in maxLOD);
                }

                if (Data.AutoGenerateMipmaps)
                    GenerateMipmaps();

                if (allowPostPushCallback)
                    OnPostPushData();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                IsPushing = false;
                Unbind();
            }
        }

        private unsafe void PushMipmap(GLEnum glTarget, int i, MipmapInfo? info, EPixelInternalFormat? internalFormatForce)
        {
            if (!Data.Resizable && !StorageSet)
            {
                Debug.LogWarning("Texture storage not set on non-resizable texture, can't push mipmaps.");
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
                internalPixelFormat = ToInternalFormat(internalFormatForce ?? EPixelInternalFormat.Rgb);
                pixelFormat = GLEnum.Rgb;
                pixelType = GLEnum.UnsignedByte;
                data = null;
                fullPush = false;
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
                PushWithData(glTarget, i, mip!.Width, mip.Height, pixelFormat, pixelType, internalPixelFormat, data, pbo, fullPush);
            else
                PushWithNoData(glTarget, i, Data.Width >> i, Data.Height >> i, pixelFormat, pixelType, internalPixelFormat, fullPush);

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
                    Debug.LogWarning("Multisample textures do not support initial data, ignoring all mipmaps.");

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

            int uWrap = (int)ToGLEnum(Data.UWrap);
            Api.TextureParameterI(BindingId, GLEnum.TextureWrapS, in uWrap);

            int vWrap = (int)ToGLEnum(Data.VWrap);
            Api.TextureParameterI(BindingId, GLEnum.TextureWrapT, in vWrap);

        }

        public override void PreSampling()
            => Data.GrabPass?.Grab(XRFrameBuffer.BoundForWriting, Engine.Rendering.State.RenderingPipelineState?.WindowViewport);

        protected internal override void PostGenerated()
        {
            static void SetFullPush(MipmapInfo m)
                => m.NeedsFullPush = true;
            Mipmaps.ForEach(SetFullPush);
            StorageSet = false;
            base.PostGenerated();
        }
        protected internal override void PostDeleted()
        {
            // Track VRAM deallocation
            if (_allocatedVRAMBytes > 0)
            {
                Engine.Rendering.Stats.RemoveTextureAllocation(_allocatedVRAMBytes);
                _allocatedVRAMBytes = 0;
            }
            StorageSet = false;
            base.PostDeleted();
        }

        /// <summary>
        /// Calculates the approximate VRAM size for a 2D texture including all mipmap levels.
        /// </summary>
        private static long CalculateTextureVRAMSize(uint width, uint height, uint mipLevels, ESizedInternalFormat format, uint sampleCount)
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
        private static uint GetBytesPerPixel(ESizedInternalFormat format)
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