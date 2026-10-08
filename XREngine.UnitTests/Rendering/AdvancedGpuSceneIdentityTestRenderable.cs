using XREngine.Rendering.Info;

namespace XREngine.UnitTests.Rendering;

internal sealed class AdvancedGpuSceneIdentityTestRenderable : IRenderable
{
    public RenderInfo[] RenderedObjects { get; set; } = [];
}
