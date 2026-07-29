using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

internal sealed class AdvancedPreparationSceneContextProbe
    : IRuntimeRenderCommandSceneContext
{
    public uint VisibleDraws { get; private set; }
    public uint VisibleInstances { get; private set; }

    public void RenderGpuPass(IRuntimeGpuRenderPassHost gpuPass)
    {
    }

    public void RecordGpuVisibility(uint draws, uint instances)
    {
        VisibleDraws = draws;
        VisibleInstances = instances;
    }
}
