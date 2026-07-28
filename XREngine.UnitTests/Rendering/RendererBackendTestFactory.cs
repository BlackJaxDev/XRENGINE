using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

internal sealed class RendererBackendTestFactory(IRuntimeRendererHost renderer) : IRendererBackendFactory
{
    public int CreateCount { get; private set; }

    public IRuntimeRenderWindowHost? LastWindow { get; private set; }
    public long LastModuleGeneration { get; private set; }

    public IRuntimeRendererHost Create(in RendererBackendCreateContext context)
    {
        CreateCount++;
        LastWindow = context.Window;
        LastModuleGeneration = context.ModuleGeneration;
        return renderer;
    }
}
