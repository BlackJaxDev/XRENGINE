using Extensions;
using Silk.NET.OpenGL;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public class GLTexture3D(OpenGLRenderer renderer, XRTexture3D data) : GLTexture<XRTexture3D>(renderer, data)
    {
        private bool _storageSet = false;
        public bool StorageSet
        {
            get => _storageSet;
            private set => SetField(ref _storageSet, value);
        }

        protected override void DataPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.DataPropertyChanged(sender, e);
            // Handle any 3D texture specific property changes here
        }

        public override ETextureTarget TextureTarget { get; } = ETextureTarget.Texture3D;

        protected override void UnlinkData()
        {
            base.UnlinkData();

            Data.Resized -= DataResized;
        }

        protected override void LinkData()
        {
            base.LinkData();

            Data.Resized += DataResized;
        }

        private void DataResized()
        {
            StorageSet = false;
            Invalidate();
        }

        public override unsafe void PushData()
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

                var glTarget = ToGLEnum(TextureTarget);

                Api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

                EPixelInternalFormat? internalFormatForce = null;
                if (!Data.Resizable && !StorageSet)
                {
                    Api.TextureStorage3D(BindingId, (uint)Data.SmallestMipmapLevel, ToGLEnum(Data.SizedInternalFormat), Data.Width, Data.Height, Data.Depth);
                    internalFormatForce = ToBaseInternalFormat(Data.SizedInternalFormat);
                    StorageSet = true;
                }

                // For now, push a single mipmap level (level 0)
                PushMipmap(glTarget, 0, internalFormatForce);

                int baseLevel = 0;
                int maxLevel = 1000;
                int minLOD = -1000;
                int maxLOD = 1000;

                Api.TextureParameterI(BindingId, GLEnum.TextureBaseLevel, in baseLevel);
                Api.TextureParameterI(BindingId, GLEnum.TextureMaxLevel, in maxLevel);
                Api.TextureParameterI(BindingId, GLEnum.TextureMinLod, in minLOD);
                Api.TextureParameterI(BindingId, GLEnum.TextureMaxLod, in maxLOD);

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

        private unsafe void PushMipmap(GLEnum glTarget, int level, EPixelInternalFormat? internalFormatForce)
        {
            if (!Data.Resizable && !StorageSet)
            {
                Debug.LogWarning("Texture storage not set on non-resizable texture, can't push mipmaps.");
                return;
            }

            GLEnum pixelFormat = GLEnum.Rgba;
            GLEnum pixelType = GLEnum.UnsignedByte;
            InternalFormat internalPixelFormat = ToInternalFormat(internalFormatForce ?? EPixelInternalFormat.Rgba8);
            DataSource? data = null;
            bool fullPush = !StorageSet;

            uint width = (uint)(Data.Width >> level);
            uint height = (uint)(Data.Height >> level);
            uint depth = (uint)(Data.Depth >> level);

            if (data is not null && data.Length > 0)
                PushWithData(glTarget, level, width, height, depth, pixelFormat, pixelType, internalPixelFormat, data, fullPush);
            else
                PushWithNoData(glTarget, level, width, height, depth, pixelFormat, pixelType, internalPixelFormat, fullPush);
        }

        private unsafe void PushWithNoData(
            GLEnum glTarget,
            int level,
            uint width,
            uint height,
            uint depth,
            GLEnum pixelFormat,
            GLEnum pixelType,
            InternalFormat internalPixelFormat,
            bool fullPush)
        {
            if (!fullPush || !Data.Resizable)
                return;
            
            Api.TexImage3D(glTarget, level, internalPixelFormat, width, height, depth, 0, pixelFormat, pixelType, IntPtr.Zero.ToPointer());
        }

        private unsafe void PushWithData(
            GLEnum glTarget,
            int level,
            uint width,
            uint height,
            uint depth,
            GLEnum pixelFormat,
            GLEnum pixelType,
            InternalFormat internalPixelFormat,
            DataSource data,
            bool fullPush)
        {
            if (!fullPush || StorageSet)
            {
                Api.TexSubImage3D(glTarget, level, 0, 0, 0, width, height, depth, pixelFormat, pixelType, data.Address.Pointer);
            }
            else
            {
                Api.TexImage3D(glTarget, level, internalPixelFormat, width, height, depth, 0, pixelFormat, pixelType, data.Address.Pointer);
            }
        }

        protected override void SetParameters()
        {
            base.SetParameters();

            Api.TextureParameter(BindingId, GLEnum.TextureLodBias, Data.LodBias);

            int magFilter = (int)ToGLEnum(Data.MagFilter);
            Api.TextureParameterI(BindingId, GLEnum.TextureMagFilter, in magFilter);

            int minFilter = (int)ToGLEnum(Data.MinFilter);
            Api.TextureParameterI(BindingId, GLEnum.TextureMinFilter, in minFilter);

            int uWrap = (int)ToGLEnum(Data.UWrap);
            Api.TextureParameterI(BindingId, GLEnum.TextureWrapS, in uWrap);

            int vWrap = (int)ToGLEnum(Data.VWrap);
            Api.TextureParameterI(BindingId, GLEnum.TextureWrapT, in vWrap);

            int wWrap = (int)ToGLEnum(Data.WWrap);
            Api.TextureParameterI(BindingId, GLEnum.TextureWrapR, in wWrap);
        }

        public override void PreSampling()
        {
            // 3D textures don't have grab passes like 2D textures
        }

        protected internal override void PostGenerated()
        {
            StorageSet = false;
            base.PostGenerated();
        }

        protected internal override void PostDeleted()
        {
            StorageSet = false;
            base.PostDeleted();
        }
    }
}