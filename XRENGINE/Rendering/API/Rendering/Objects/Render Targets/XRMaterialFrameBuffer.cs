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

        public XRMaterialFrameBuffer() { }

        public XRMaterialFrameBuffer(XRMaterial? material)
        {
            _material = material;
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

                SetRenderTargets(_material = value);
                VerifyTextures();
            }
        }

        private void VerifyTextures()
        {
            uint? w = null;
            uint? h = null;
            if (Material != null)
            {
                foreach (var tex in Material.Textures)
                {
                    if (tex?.FrameBufferAttachment is null)
                        continue;

                    uint tw;
                    uint th;
                    if (tex is XRTexture2D tref)
                    {
                        tw = tref.Width;
                        th = tref.Height;
                    }
                    //else if (tex is XRTextureView2D vref) //TextureView derives from Texture2D
                    //{
                    //    tw = vref.Width;
                    //    th = vref.Height;
                    //}
                    else
                        continue;

                    if (w is null)
                        w = tw;
                    else if (w != tw)
                        Debug.LogWarning($"FBO texture widths are not all the same.");

                    if (h is null)
                        h = th;
                    else if (h != th)
                        Debug.LogWarning($"FBO texture heights are not all the same.");
                }
            }
            if (w is not null && h is not null)
                Resize(w.Value, h.Value);
        }
    }
}
