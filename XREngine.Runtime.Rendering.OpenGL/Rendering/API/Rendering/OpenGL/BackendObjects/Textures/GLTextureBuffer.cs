using Silk.NET.OpenGL;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    /// <summary>
    /// OpenGL wrapper for buffer textures (texel buffers).
    /// </summary>
    public class GLTextureBuffer(OpenGLRenderer renderer, XRTextureBuffer data) : GLTexture<XRTextureBuffer>(renderer, data)
    {
        public override ETextureTarget TextureTarget => ETextureTarget.TextureBuffer;

        protected override void DataPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.DataPropertyChanged(sender, e);

            switch (e.PropertyName)
            {
                case nameof(XRTextureBuffer.DataBuffer):
                case nameof(XRTextureBuffer.SizedInternalFormat):
                    Invalidate();
                    break;
            }
        }

        public override void PushData()
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
                var dataBuffer = Data.DataBuffer;
                var glBuffer = Renderer.GenericToAPI<GLDataBuffer>(dataBuffer);
                if (glBuffer is null)
                    return;

                if (!glBuffer.TryGetBindingId(out var bufferId))
                {
                    glBuffer.Generate();
                    if (!glBuffer.TryGetBindingId(out bufferId))
                        return;
                }

                // Ensure the underlying buffer has storage and latest data before binding to the texture buffer target.
                glBuffer.PushData();

                Bind();
                Api.TextureBuffer(BindingId, ToGLEnum(Data.SizedInternalFormat), bufferId);

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
