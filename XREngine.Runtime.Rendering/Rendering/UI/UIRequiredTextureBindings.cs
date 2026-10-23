using XREngine.Data.Rendering;

namespace XREngine.Rendering.UI;

/// <summary>Publishes one required sampled texture for a retained UI draw.</summary>
internal sealed class UIRequiredTextureBindings(XRTexture texture) : IRenderResourceBindingPublisher
{
    public ERenderBindingFrequency Frequency => ERenderBindingFrequency.Object;
    public ulong Generation => 1;
    public ulong ResourceGeneration => 1;
    public bool RequiresReadyDescriptorResources => true;

    public void PublishUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram) { }

    public void PublishResources(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
    {
        texture.SampleIn(materialProgram, 0);
        if (!ReferenceEquals(vertexProgram, materialProgram))
            texture.SampleIn(vertexProgram, 0);
    }
}
