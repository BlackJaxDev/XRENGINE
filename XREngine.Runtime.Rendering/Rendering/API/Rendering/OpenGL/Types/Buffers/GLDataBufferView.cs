using XREngine.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    /// <summary>
    /// Minimal wrapper for XRDataBufferView. OpenGL does not expose buffer views directly;
    /// this object forwards to the underlying GLDataBuffer for binding.
    /// </summary>
    public class GLDataBufferView(OpenGLRenderer renderer, XRDataBufferView data) : GLObject<XRDataBufferView>(renderer, data)
    {
        public override EGLObjectType Type => EGLObjectType.Buffer;

        protected override void LinkData() { }
        protected override void UnlinkData() { }

        public override bool TryGetBindingId(out uint bindingId)
        {
            var buffer = Renderer.GenericToAPI<GLDataBuffer>(Data.Buffer);
            if (buffer is null)
            {
                bindingId = InvalidBindingId;
                return false;
            }
            return buffer.TryGetBindingId(out bindingId);
        }

        protected internal override void PreGenerated()
        {
            // Ensure we don't try to generate a separate GL object; reuse buffer binding id.
            _bindingId = InvalidBindingId;
        }

        protected override uint CreateObject() => InvalidBindingId;
    }
}
