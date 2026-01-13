using Silk.NET.OpenGL;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    /// <summary>
    /// Provides a view into an existing texture's data.
    /// </summary>
    /// <param name="renderer"></param>
    /// <param name="data"></param>
    public class GLTextureView(OpenGLRenderer renderer, XRTextureViewBase data) : GLTexture<XRTextureViewBase>(renderer, data)
    {
        public override EGLObjectType Type => EGLObjectType.Texture;

        public override ETextureTarget TextureTarget => Data.TextureTarget;

        private IGLTexture? GetViewedTexture()
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

        //Can't re-link the texture view, so we need to re-create it if it changes.
        protected internal override void PostGenerated()
        {
            base.PostGenerated();

            IGLTexture? viewed = GetViewedTexture();
            if (viewed is null)
                return;

            // `glTextureView` requires origtexture to be a valid texture object.
            // With `glGenTextures`, the name may not become a real texture object until bound once.
            // Bind/push the viewed texture here (and restore prior binding) to avoid invalid origtexture.
            var previous = Renderer.BoundTexture;
            viewed.Bind();
            if (previous is null)
            {
                Renderer.BoundTexture = null;
                Api.BindTexture(ToGLEnum(viewed.TextureTarget), 0);
            }
            else
                previous.Bind();

            Api.TextureView(
                BindingId,
                ToGLEnum(Data.TextureTarget),
                viewed.BindingId,
                ToGLEnum(Data.InternalFormat),
                Data.MinLevel,
                Data.NumLevels,
                Data.MinLayer,
                Data.NumLayers);

            SetParameters();
            IsInvalidated = false;
        }

        public override void PushData()
        {
            //if (IsPushing)
            //    return;

            //IsPushing = true;

            //// The value of GL_TEXTURE_IMMUTABLE_FORMAT for origtexture must be GL_TRUE. 
            //var viewed = GetViewedTexture();
            //if (viewed is null)
            //{
            //    IsPushing = false;
            //    return;
            //}

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
            //Api.TextureView(BindingId, ToGLEnum(viewed.TextureTarget), viewed.BindingId, ToGLEnum(Data.InternalFormat), Data.MinLevel, Data.NumLevels, Data.MinLayer, Data.NumLayers);

            //switch (Data)
            //{
            //    case XRTexture2DView t2d:
            //        {
            //            int dsmode = t2d.DepthStencilViewFormat == EDepthStencilFmt.Stencil ? (int)GLEnum.StencilIndex : (int)GLEnum.DepthComponent;
            //            Api.TextureParameterI(BindingId, GLEnum.DepthStencilTextureMode, in dsmode);
            //            break;
            //        }

            //    case XRTexture2DArrayView t2da:
            //        {
            //            int dsmode = t2da.DepthStencilViewFormat == EDepthStencilFmt.Stencil ? (int)GLEnum.StencilIndex : (int)GLEnum.DepthComponent;
            //            Api.TextureParameterI(BindingId, GLEnum.DepthStencilTextureMode, in dsmode);
            //            break;
            //        }
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