using Silk.NET.OpenGL;
using XREngine.Data;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    /// <summary>
    /// OpenGL wrapper for rectangle textures (non-mipmapped 2D textures).
    /// </summary>
    public class GLTextureRectangle(OpenGLRenderer renderer, XRTextureRectangle data) : GLTexture<XRTextureRectangle>(renderer, data)
    {
        public override ETextureTarget TextureTarget => ETextureTarget.TextureRectangle;
        private bool _storageAllocated;

        protected override void SetParameters()
        {
            base.SetParameters();

            int magFilter = (int)ToGLEnum(Data.MagFilter);
            Api.TextureParameterI(BindingId, GLEnum.TextureMagFilter, in magFilter);

            int minFilter = (int)ToGLEnum(Data.MinFilter);
            Api.TextureParameterI(BindingId, GLEnum.TextureMinFilter, in minFilter);

            int uWrap = (int)ToGLEnum(Data.UWrap);
            Api.TextureParameterI(BindingId, GLEnum.TextureWrapS, in uWrap);

            int vWrap = (int)ToGLEnum(Data.VWrap);
            Api.TextureParameterI(BindingId, GLEnum.TextureWrapT, in vWrap);

            Api.TextureParameter(BindingId, GLEnum.TextureLodBias, Data.LodBias);
        }

        public override unsafe void PushData()
        {
            if (IsPushing)
                return;

            OnPrePushData(out bool shouldPush, out bool allowPostPushCallback);
            if (!shouldPush)
            {
                if (allowPostPushCallback)
                    OnPostPushData();
                return;
            }

            IsPushing = true;
            try
            {
                Bind();
                Api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

                var glTarget = ToGLEnum(TextureTarget);
                var pixelFmt = ToGLEnum(Data.PixelFormat);
                var pixelType = ToGLEnum(Data.PixelType);
                var internalFmt = ToInternalFormat(ToBaseInternalFormat(Data.SizedInternalFormat));

                bool useTexImage = Data.Resizable || !_storageAllocated;

                if (!Data.Resizable && !_storageAllocated)
                {
                    Api.TextureStorage2D(BindingId, 1, ToGLEnum(Data.SizedInternalFormat), Data.Width, Data.Height);
                    _storageAllocated = true;
                }

                var cpuData = Data.Data;
                var pbo = Data.StreamingPBO;

                if (pbo is not null && pbo.Target != EBufferTarget.PixelUnpackBuffer)
                    throw new ArgumentException("StreamingPBO must target PixelUnpackBuffer for rectangle uploads.");

                if (useTexImage)
                {
                    if (pbo is not null)
                    {
                        pbo.Bind();
                        Api.TexImage2D(glTarget, 0, internalFmt, Data.Width, Data.Height, 0, pixelFmt, pixelType, null);
                        pbo.Unbind();
                    }
                    else
                    {
                        void* ptr = cpuData is null ? null : cpuData.Address.Pointer;
                        Api.TexImage2D(glTarget, 0, internalFmt, Data.Width, Data.Height, 0, pixelFmt, pixelType, ptr);
                    }
                }
                else if (cpuData is not null || pbo is not null)
                {
                    if (pbo is not null)
                    {
                        pbo.Bind();
                        Api.TexSubImage2D(glTarget, 0, 0, 0, Data.Width, Data.Height, pixelFmt, pixelType, null);
                        pbo.Unbind();
                    }
                    else
                        Api.TexSubImage2D(glTarget, 0, 0, 0, Data.Width, Data.Height, pixelFmt, pixelType, cpuData!.Address.Pointer);
                }

                if (allowPostPushCallback)
                    OnPostPushData();
            }
            finally
            {
                IsPushing = false;
                Unbind();
            }
        }
    }
}
