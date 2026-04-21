using XREngine.Components;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Info;

namespace XREngine
{
    /// <summary>
    /// Defines an object with any number of cullable sub-objects that can be rendered.
    /// </summary>
    public interface IRenderable : IRenderableBase
    {
        RenderInfo[] RenderedObjects { get; }
        float IRenderableBase.TransformDepth => (this as XRComponent)?.Transform?.Depth ?? 0;
        protected bool IsShadowPass => RuntimeRenderingHostServices.Current.IsShadowPass;
    }
}
