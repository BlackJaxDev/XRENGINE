using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public delegate void DelSetUniforms(XRRenderProgram materialProgram);
    /// <summary>
    /// Sets this FBO's render targets to the textures in the provided material using their FrameBufferAttachment properties.
    /// </summary>
    public class XRMaterialFrameBuffer : XRFrameBuffer
    {
        private XRMaterial? _material;
        private bool _deriveRenderTargetsFromMaterial = true;

        public XRMaterialFrameBuffer() { }

        public XRMaterialFrameBuffer(XRMaterial? material, bool deriveRenderTargetsFromMaterial = true)
        {
            _deriveRenderTargetsFromMaterial = deriveRenderTargetsFromMaterial;
            _material = material;
            if (_deriveRenderTargetsFromMaterial)
                SetRenderTargets(_material);
            VerifyTextures();
        }

        public XRMaterialFrameBuffer(
            XRMaterial material,
            params (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[]? targets)
            : this(material) => SetRenderTargets(targets);

        public XRMaterial? Material
        {
            get => _material;
            set
            {
                if (_material == value)
                    return;

                _material = value;
                if (_deriveRenderTargetsFromMaterial)
                    SetRenderTargets(_material);
                VerifyTextures();
            }
        }

        /// <summary>
        /// When true, framebuffer targets are inferred from the material's attachable textures.
        /// Disable this for fullscreen passes that sample from render targets of mixed sizes but
        /// write to an explicitly assigned output attachment.
        /// </summary>
        public bool DeriveRenderTargetsFromMaterial
        {
            get => _deriveRenderTargetsFromMaterial;
            set
            {
                if (_deriveRenderTargetsFromMaterial == value)
                    return;

                _deriveRenderTargetsFromMaterial = value;
                if (_deriveRenderTargetsFromMaterial)
                    SetRenderTargets(_material);

                VerifyTextures();
            }
        }

        private void VerifyTextures()
        {
            uint? w = null;
            uint? h = null;
            if (Targets is not null)
            {
                foreach (var (target, _, _, _) in Targets)
                {
                    uint tw;
                    uint th;
                    if (target is XRTexture2D tref)
                    {
                        tw = tref.Width;
                        th = tref.Height;
                    }
                    //else if (tex is XRTextureView2D vref) //TextureView derives from Texture2D
                    //{
                    //    tw = vref.Width;
                    //    th = vref.Height;
                    //}
                    else if (target is XRRenderBuffer rb)
                    {
                        tw = rb.Width;
                        th = rb.Height;
                    }
                    else
                        continue;

                    if (w is null)
                        w = tw;
                    else if (w != tw)
                        RuntimeRenderingHostServices.Current.LogWarning("FBO texture widths are not all the same.");

                    if (h is null)
                        h = th;
                    else if (h != th)
                        RuntimeRenderingHostServices.Current.LogWarning("FBO texture heights are not all the same.");
                }
            }
            if (w is not null && h is not null)
                Resize(w.Value, h.Value);
        }
    }
}
