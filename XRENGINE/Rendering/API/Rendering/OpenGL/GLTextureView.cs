using Silk.NET.OpenGL;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public class GLTextureView(OpenGLRenderer renderer, XRTextureViewBase data) : GLTexture<XRTextureViewBase>(renderer, data)
    {
        public override GLObjectType Type => GLObjectType.Texture;

        public override ETextureTarget TextureTarget
        {
            get
            {
                IGLTexture? apiViewed = GetTexture();
                return apiViewed is null ? ETextureTarget.Texture2D : apiViewed.TextureTarget;
            }
        }

        protected internal override void PostGenerated()
        {
            base.PostGenerated();

            IGLTexture? apiViewed = GetTexture();
            if (apiViewed is null)
                return;

            Api.TextureView(
                BindingId,
                ToGLEnum(Data.TextureTarget),
                apiViewed.BindingId,
                ToGLEnum(Data.InternalFormat),
                Data.MinLevel,
                Data.NumLevels,
                Data.MinLayer,
                Data.NumLayers);
        }

        private IGLTexture? GetTexture()
            => !Renderer.TryGetAPIRenderObject(Data.GetViewedTexture(), out var apiObject) || apiObject is not IGLTexture apiViewed ? null : apiViewed;

        protected override void SetParameters()
        {
            base.SetParameters();

            if (Data is XRTexture2DView t2d)
            {
                int dsmode = t2d.DepthStencilViewFormat == EDepthStencilFmt.Stencil ? (int)GLEnum.StencilIndex : (int)GLEnum.DepthComponent;
                Api.TextureParameterI(BindingId, GLEnum.DepthStencilTextureMode, in dsmode);
            }
            else if (Data is XRTexture2DArrayView t2da)
            {
                int dsmode = t2da.DepthStencilViewFormat == EDepthStencilFmt.Stencil ? (int)GLEnum.StencilIndex : (int)GLEnum.DepthComponent;
                Api.TextureParameterI(BindingId, GLEnum.DepthStencilTextureMode, in dsmode);
            }

            if (!IsMultisampleTarget)
            {
                Api.TextureParameter(BindingId, GLEnum.TextureLodBias, Data.LodBias);

                int magFilter = (int)ToGLEnum(Data.MagFilter);
                Api.TextureParameterI(BindingId, GLEnum.TextureMagFilter, in magFilter);

                int minFilter = (int)ToGLEnum(Data.MinFilter);
                Api.TextureParameterI(BindingId, GLEnum.TextureMinFilter, in minFilter);

                int uWrap = (int)ToGLEnum(Data.UWrap);
                Api.TextureParameterI(BindingId, GLEnum.TextureWrapS, in uWrap);

                int vWrap = (int)ToGLEnum(Data.VWrap);
                Api.TextureParameterI(BindingId, GLEnum.TextureWrapT, in vWrap);
            }
        }

        public override void PushData()
        {
            //if (IsPushing)
            //    return;

            //IsPushing = true;

            //// The value of GL_TEXTURE_IMMUTABLE_FORMAT for origtexture must be GL_TRUE. 
            //var viewed = Data.GetViewedTexture();
            //if (!Renderer.TryGetAPIRenderObject(viewed, out var apiObject) || apiObject is not IGLTexture apiViewed)
            //    return;

            //OnPrePushData(out bool shouldPush, out bool allowPostPushCallback);
            //if (!shouldPush)
            //{
            //    if (allowPostPushCallback)
            //        OnPostPushData();
            //    IsPushing = false;
            //    return;
            //}

            //// When the original texture's target is GL_TEXTURE_CUBE_MAP, the layer parameters are interpreted in the same order as if it were a GL_TEXTURE_CUBE_MAP_ARRAY with 6 layer-faces. 
            //// If target is GL_TEXTURE_1D, GL_TEXTURE_2D, GL_TEXTURE_3D, GL_TEXTURE_RECTANGLE, or GL_TEXTURE_2D_MULTISAMPLE, numlayers must equal 1. 
            //Api.TextureView(BindingId, ToGLEnum(apiViewed.TextureTarget), apiViewed.BindingId, ToGLEnum(Data.InternalFormat), Data.MinLevel, Data.NumLevels, Data.MinLayer, Data.NumLayers);

            //if (Data is XRTexture2DView t2d)
            //{
            //    int dsmode = t2d.DepthStencilViewFormat == EDepthStencilFmt.Stencil ? (int)GLEnum.StencilIndex : (int)GLEnum.DepthComponent;
            //    Api.TextureParameterI(BindingId, GLEnum.DepthStencilTextureMode, in dsmode);
            //}

            //if (allowPostPushCallback)
            //    OnPostPushData();

            //IsPushing = false;
        }

        protected override void LinkData()
            => Data.ViewedTextureChanged += Invalidate;
        protected override void UnlinkData()
            => Data.ViewedTextureChanged -= Invalidate;
    }
}