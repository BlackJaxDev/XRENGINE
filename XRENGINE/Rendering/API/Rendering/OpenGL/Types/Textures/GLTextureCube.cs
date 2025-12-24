using Extensions;
using Silk.NET.OpenGL;
using System.ComponentModel;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.OpenGL;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.Models.Materials.Textures
{
    public class GLTextureCube(OpenGLRenderer renderer, XRTextureCube data) : GLTexture<XRTextureCube>(renderer, data)
    {
        private bool _storageSet = false;

        public class MipmapInfo : XRBase
        {
            private bool _hasPushedResizedData = false;
            //private bool _hasPushedUpdateData = false;
            private readonly CubeMipmap _mipmap;
            private readonly GLTextureCube _texture;

            public CubeMipmap Mipmap => _mipmap;

            public MipmapInfo(GLTextureCube texture, CubeMipmap mipmap)
            {
                _texture = texture;
                _mipmap = mipmap;

                foreach (var side in _mipmap.Sides)
                    side.PropertyChanged += MipmapPropertyChanged;
            }
            ~MipmapInfo()
            {
                foreach (var side in _mipmap.Sides)
                    side.PropertyChanged -= MipmapPropertyChanged;
            }

            private void MipmapPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                switch (e.PropertyName)
                {
                    case nameof(Mipmap2D.Data):
                        //HasPushedUpdateData = false;
                        _texture.Invalidate();
                        break;
                    case nameof(Mipmap2D.Width):
                    case nameof(Mipmap2D.Height):
                    case nameof(Mipmap2D.InternalFormat):
                    case nameof(Mipmap2D.PixelFormat):
                    case nameof(Mipmap2D.PixelType):
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
                case nameof(Data.Mipmaps):
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

        public override ETextureTarget TextureTarget => ETextureTarget.TextureCubeMap;

        protected override void UnlinkData()
        {
            base.UnlinkData();

            Data.AttachFaceToFBORequested -= AttachFaceToFBO;
            Data.DetachFaceFromFBORequested -= DetachFaceFromFBO;
            Data.Resized -= DataResized;
            Mipmaps = [];
        }

        protected override void LinkData()
        {
            base.LinkData();

            Data.AttachFaceToFBORequested += AttachFaceToFBO;
            Data.DetachFaceFromFBORequested += DetachFaceFromFBO;
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

        private void DetachFaceFromFBO(XRFrameBuffer target, EFrameBufferAttachment attachment, ECubemapFace face, int mipLevel)
        {
            if (Renderer.GetOrCreateAPIRenderObject(target) is not GLObjectBase apiFBO)
                return;

            Api.NamedFramebufferTextureLayer(apiFBO.BindingId, ToGLEnum(attachment), 0, mipLevel, (int)face);
            //Api.FramebufferTexture2D(GLEnum.Framebuffer, ToGLEnum(attachment), GLEnum.TextureCubeMapPositiveX + (int)face, 0, mipLevel);
        }
        public void AttachFaceToFBO(XRFrameBuffer fbo, EFrameBufferAttachment attachment, ECubemapFace face, int mipLevel)
        {
            if (Renderer.GetOrCreateAPIRenderObject(fbo) is not GLObjectBase apiFBO)
                return;

            Api.NamedFramebufferTextureLayer(apiFBO.BindingId, ToGLEnum(attachment), BindingId, mipLevel, (int)face);
            //Api.FramebufferTexture2D(GLEnum.Framebuffer, ToGLEnum(attachment), GLEnum.TextureCubeMapPositiveX + (int)face, BindingId, mipLevel);
        }

        public unsafe override void PushData()
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

                Api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

                EPixelInternalFormat? internalFormatForce = null;
                if (!Data.Resizable && !_storageSet)
                {
                    Api.TexStorage2D(GLEnum.TextureCubeMap, (uint)Data.SmallestMipmapLevel, ToGLEnum(Data.SizedInternalFormat), Data.Extent, Data.Extent);
                    internalFormatForce = ToBaseInternalFormat(Data.SizedInternalFormat);
                    _storageSet = true;
                }

                if (Mipmaps is null || Mipmaps.Length == 0)
                    PushMipmap(0, null, internalFormatForce);
                else
                {
                    for (int i = 0; i < Mipmaps.Length; ++i)
                        PushMipmap(i, Mipmaps[i], internalFormatForce);
                }

                int baseLevel = 0;
                int maxLevel = 1000;
                int minLOD = -1000;
                int maxLOD = 1000;

                Api.TextureParameterI(BindingId, GLEnum.TextureBaseLevel, in baseLevel);
                Api.TextureParameterI(BindingId, GLEnum.TextureMaxLevel, in maxLevel);
                Api.TextureParameterI(BindingId, GLEnum.TextureMinLod, in minLOD);
                Api.TextureParameterI(BindingId, GLEnum.TextureMaxLod, in maxLOD);

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

        private unsafe void PushMipmap(int i, MipmapInfo? info, EPixelInternalFormat? internalFormatForce)
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
            CubeMipmap? cubeMip = info?.Mipmap;

            for (int side = 0; side < 6; ++side)
            {
                var mip = cubeMip?.Sides[side];
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

                GLEnum glTarget = GLEnum.TextureCubeMapPositiveX + side;
                if (data is null || data.Length == 0)
                    Push(glTarget, i, Data.Extent >> i, Data.Extent >> i, pixelFormat, pixelType, internalPixelFormat, hasPushedResized);
                else
                {
                    Push(glTarget, i, mip!.Width, mip.Height, pixelFormat, pixelType, internalPixelFormat, data, hasPushedResized);
                    //data?.Dispose();
                }
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
                Api.TexImage2D(glTarget, i, internalPixelFormat, w, h, 0, pixelFormat, pixelType, IntPtr.Zero.ToPointer());
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
                Api.TexImage2D(glTarget, i, internalPixelFormat, w, h, 0, pixelFormat, pixelType, ptr);
        }
    }
}
