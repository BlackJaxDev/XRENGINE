using XREngine.Rendering.GI.Contracts;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.GI.LightProbes;

/// <summary>
/// Registry-owned module identity for the existing probe/IBL lighting path.
/// Probe resources remain part of the host's generic PBR-lighting contract;
/// this module intentionally contributes no screen field, algorithm resources,
/// or command sequence.
/// </summary>
public sealed class LightProbesAndIblGlobalIlluminationModule : IGlobalIlluminationModule
{
    public GlobalIlluminationProviderDescriptor Descriptor
        => GlobalIlluminationProviderRegistry.GetRequiredDescriptor(EGlobalIlluminationMode.LightProbesAndIbl);

    public GlobalIlluminationSupportResult EvaluateSupport(in GlobalIlluminationModuleContext context)
        => context.Plan.Support;

    public RenderPipelineResourceVariant BuildResourceVariant(XRViewport? viewport) => default;
    public void DeclareResources(RenderPipelineResourceLayoutBuilder builder, in GlobalIlluminationModuleContext context) { }
    public void ContributePasses(ViewportRenderCommandContainer commands, in GlobalIlluminationModuleContext context) { }
    public void Invalidate(string reason) { }
    public void Release() { }
}
