using Extensions;
using Silk.NET.OpenGL;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL
{
    public class GLTexture2D(OpenGLRenderer renderer, XRTexture2D data) : GLTexture<XRTexture2D>(renderer, data)
    {
        private bool _storageSet = false;

        public class MipmapInfo : XRBase
        {
            private bool _hasPushedResizedData = false;
            //private bool _hasPushedUpdateData = false;
            private readonly Mipmap2D _mipmap;
            private readonly GLTexture2D _texture;

            public Mipmap2D Mipmap => _mipmap;

            public MipmapInfo(GLTexture2D texture, Mipmap2D mipmap)
            {
                _texture = texture;
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
                _texture.Invalidate();
            }

            private void MipmapPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                switch (e.PropertyName)
                {
                    case nameof(Mipmap.Data):
                        //HasPushedUpdateData = false;
                        _texture.Invalidate();
                        break;
                    case nameof(Mipmap.Width):
                    case nameof(Mipmap.Height):
                    case nameof(Mipmap.InternalFormat):
                    case nameof(Mipmap.PixelFormat):
                    case nameof(Mipmap.PixelType):
                        HasPushedResizedData = false;
                        //HasPushedUpdateData = false;
                        _texture.Invalidate();
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

        public override ETextureTarget TextureTarget => Data.MultiSample ? ETextureTarget.Texture2DMultisample : ETextureTarget.Texture2D;

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
            _storageSet = false;
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
            base.PostGenerated();
        }
        protected internal override void PostDeleted()
        {
            _storageSet = false;
            base.PostDeleted();
        }

        //TODO: use PBO per texture for quick data updates
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
                if (!Data.Resizable && !_storageSet)
                {
                    if (Data.MultiSample)
                        Api.TextureStorage2DMultisample(BindingId, Data.MultiSampleCount, ToGLEnum(Data.SizedInternalFormat), Data.Width, Data.Height, Data.FixedSampleLocations);
                    else
                        Api.TextureStorage2D(BindingId, (uint)Data.SmallestMipmapLevel, ToGLEnum(Data.SizedInternalFormat), Data.Width, Data.Height);
                    internalFormatForce = ToBaseInternalFormat(Data.SizedInternalFormat);
                    _storageSet = true;
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
            if (!Data.Resizable && !_storageSet)
            {
                Debug.LogWarning("Texture storage not set on non-resizable texture, can't push mipmaps.");
                return;
            }

            GLEnum pixelFormat;
            GLEnum pixelType;
            InternalFormat internalPixelFormat;

            DataSource? data;
            bool hasPushedResized;
            Mipmap2D? mip = info?.Mipmap;
            if (mip is null)
            {
                internalPixelFormat = ToInternalFormat(internalFormatForce ?? EPixelInternalFormat.Rgb);
                pixelFormat = GLEnum.Rgb;
                pixelType = GLEnum.UnsignedByte;
                data = null;
                hasPushedResized = false;
            }
            else
            {
                pixelFormat = ToGLEnum(mip.PixelFormat);
                pixelType = ToGLEnum(mip.PixelType);
                internalPixelFormat = ToInternalFormat(internalFormatForce ?? mip.InternalFormat);
                data = mip.Data;
                hasPushedResized = info!.HasPushedResizedData;
            }

            if (data is null || data.Length == 0)
                Push(glTarget, i, Data.Width >> i, Data.Height >> i, pixelFormat, pixelType, internalPixelFormat, hasPushedResized);
            else
            {
                Push(glTarget, i, mip!.Width, mip.Height, pixelFormat, pixelType, internalPixelFormat, data, hasPushedResized);
                data?.Dispose();
            }

            if (info != null)
            {
                info.HasPushedResizedData = true;
                //info.HasPushedUpdateData = true;
            }
        }

        private unsafe void Push(GLEnum glTarget, int i, uint w, uint h, GLEnum pixelFormat, GLEnum pixelType, InternalFormat internalPixelFormat, bool hasPushedResized)
        {
            if (!hasPushedResized && Data.Resizable)
            {
                if (Data.MultiSample)
                    Api.TexImage2DMultisample(glTarget, Data.MultiSampleCount, internalPixelFormat, w, h, Data.FixedSampleLocations);
                else
                    Api.TexImage2D(glTarget, i, internalPixelFormat, w, h, 0, pixelFormat, pixelType, IntPtr.Zero.ToPointer());
            }
        }
        private unsafe void Push(GLEnum glTarget, int i, uint w, uint h, GLEnum pixelFormat, GLEnum pixelType, InternalFormat internalPixelFormat, DataSource bmp, bool hasPushedResized)
        {
            // If a non-zero named buffer object is bound to the GL_PIXEL_UNPACK_BUFFER target (see glBindBuffer) while a texture image is specified,
            // the data ptr is treated as a byte offset into the buffer object's data store.
            void* ptr = bmp.Address.Pointer;
            if (ptr is null)
            {
                Debug.LogWarning("Texture data source is null.");
                return;
            }

            if (hasPushedResized || _storageSet)
                Api.TexSubImage2D(glTarget, i, 0, 0, w, h, pixelFormat, pixelType, ptr);
            else
            {
                if (Data.MultiSample)
                    Api.TexImage2DMultisample(glTarget, Data.MultiSampleCount, internalPixelFormat, w, h, Data.FixedSampleLocations);
                else
                    Api.TexImage2D(glTarget, i, internalPixelFormat, w, h, 0, pixelFormat, pixelType, ptr);
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
        {
            Data.GrabPass?.Grab(XRFrameBuffer.BoundForWriting, Engine.Rendering.State.RenderingPipelineState?.WindowViewport);
        }
    }
}