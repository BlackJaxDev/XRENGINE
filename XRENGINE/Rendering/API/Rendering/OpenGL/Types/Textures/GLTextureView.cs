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
            => Renderer.GetOrCreateAPIRenderObject(Data.GetViewedTexture()) is IGLTexture apiViewed ? apiViewed : null;

        private bool IsCompatibleViewTarget(ETextureTarget viewedTarget, ETextureTarget viewTarget)
        {
            if (viewedTarget == viewTarget)
                return true;

            return (viewedTarget, viewTarget) switch
            {
                (ETextureTarget.TextureCubeMap, ETextureTarget.Texture2D) when Data.NumLayers == 1 => true,
                (ETextureTarget.TextureCubeMapArray, ETextureTarget.Texture2D) when Data.NumLayers == 1 => true,
                (ETextureTarget.TextureCubeMapArray, ETextureTarget.Texture2DArray) => true,
                (ETextureTarget.Texture2DArray, ETextureTarget.Texture2D) when Data.NumLayers == 1 => true,
                (ETextureTarget.Texture2DMultisampleArray, ETextureTarget.Texture2DMultisample) when Data.NumLayers == 1 => true,
                _ => false,
            };
        }

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

            if (!IsCompatibleViewTarget(viewed.TextureTarget, Data.TextureTarget))
            {
                Debug.OpenGLWarning($"[GLTextureView] Incompatible texture view target. View='{Data.Name ?? BindingId.ToString()}', RequestedTarget={Data.TextureTarget}, ViewedTarget={viewed.TextureTarget}, MinLayer={Data.MinLayer}, NumLayers={Data.NumLayers}.");
                return;
            }

            // glTextureView requires:
            //   - <texture> (destination): an unused name from glGenTextures, never bound or given a target.
            //   - <origtexture> (source): a valid texture with immutable storage (glTexStorage* called).
            //
            // Bind the SOURCE texture to ensure it becomes a real object with immutable storage.
            // Do NOT bind the destination — glGenTextures already gave us a valid unused name.
            var previous = Renderer.BoundTexture;
            viewed.Bind();
            if (previous is null)
            {
                Renderer.SetBoundTexture(viewed.TextureTarget, null);
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

            GLEnum error = Api.GetError();
            if (error != GLEnum.NoError)
            {
                Debug.OpenGLWarning($"[GLTextureView] Failed to create texture view '{Data.Name ?? BindingId.ToString()}': {error}. RequestedTarget={Data.TextureTarget}, ViewedTarget={viewed.TextureTarget}, Format={Data.InternalFormat}, MinLayer={Data.MinLayer}, NumLayers={Data.NumLayers}.");
                return;
            }

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