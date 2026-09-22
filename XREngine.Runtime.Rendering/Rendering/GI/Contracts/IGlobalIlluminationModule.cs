using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Provider-owned lifecycle surface. Modules declare their resources and graph work without referencing a concrete host pipeline.
/// </summary>
public interface IGlobalIlluminationModule
{
    GlobalIlluminationProviderDescriptor Descriptor { get; }

    GlobalIlluminationSupportResult EvaluateSupport(in GlobalIlluminationModuleContext context);
    RenderPipelineResourceVariant BuildResourceVariant(XRViewport? viewport);
    void DeclareResources(RenderPipelineResourceLayoutBuilder builder, in GlobalIlluminationModuleContext context);
    void ContributePasses(ViewportRenderCommandContainer commands, in GlobalIlluminationModuleContext context);
    void Invalidate(string reason);
    void Release();
}
