using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.UI;

/// <summary>Publishes only fixed resource identities owned by one captured draw.</summary>
internal sealed class UIFixedDrawBindings(XRDataBuffer[] buffers, XRTexture?[] textures, Vector4 bounds) : IRenderResourceBindingPublisher
{
    public ERenderBindingFrequency Frequency => ERenderBindingFrequency.Object;
    public ulong Generation => 1;
    public ulong ResourceGeneration => 1;
    public bool RequiresReadyDescriptorResources => true;
    public void PublishUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
    {
        materialProgram.Uniform("UIWidth", bounds.Z);
        materialProgram.Uniform("UIHeight", bounds.W);
        materialProgram.Uniform("UIX", bounds.X);
        materialProgram.Uniform("UIY", bounds.Y);
        materialProgram.Uniform("UIXYWH", bounds);
    }
    public void PublishResources(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
    {
        for (int index = 0; index < textures.Length; index++)
        {
            if (textures[index] is not { } texture)
                continue;
            texture.SampleIn(materialProgram, index);
            if (!ReferenceEquals(vertexProgram, materialProgram))
                texture.SampleIn(vertexProgram, index);
        }
        foreach (XRDataBuffer buffer in buffers)
        {
            if (buffer.BindingIndexOverride is not { } binding)
                throw new InvalidOperationException($"UI draw buffer '{buffer.AttributeName}' requires a fixed binding index.");
            vertexProgram.BindBuffer(buffer, binding);
        }
    }
}
