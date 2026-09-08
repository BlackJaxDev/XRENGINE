using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Capture.Lights;

public partial class LightProbeComponent
{
    private XRQuadFrameBuffer[] _irradianceMipTargets = [];
    private XRQuadFrameBuffer[] _prefilterMipTargets = [];

    private void DestroyIblMipTargets()
    {
        foreach (XRQuadFrameBuffer target in _irradianceMipTargets)
            target.Destroy();
        foreach (XRQuadFrameBuffer target in _prefilterMipTargets)
            target.Destroy();
        _irradianceMipTargets = [];
        _prefilterMipTargets = [];
    }

    private XRQuadFrameBuffer[] GetIblMipTargets(bool prefilter, XRTexture2D output)
    {
        ref XRQuadFrameBuffer[] targets = ref (prefilter ? ref _prefilterMipTargets : ref _irradianceMipTargets);
        int count = output.SmallestMipmapLevel + 1;
        if (targets.Length != count)
        {
            foreach (XRQuadFrameBuffer target in targets)
                target.Destroy();
            targets = [];
            XRQuadFrameBuffer template = (prefilter ? _prefilterFBO : _irradianceFBO)
                ?? throw new InvalidOperationException("Convolution template is missing.");
            XRTexture source = (prefilter ? _prefilterSourceTexture : _irradianceSourceTexture)
                ?? throw new InvalidOperationException("Convolution source is missing.");
            XRQuadFrameBuffer[] created = new XRQuadFrameBuffer[count];
            int createdCount = 0;
            try
            {
                for (int mip = 0; mip < count; mip++)
                {
                    ShaderVar[] parameters = !prefilter ? [] : source is XRTextureCube
                        ? CreateCubemapPrefilterShaderVars(_prefilterSourceDimension)
                        : CreatePrefilterShaderVars(_prefilterSourceDimension);
                    XRMaterial material = new(parameters, [source], template.Material!.Shaders)
                    {
                        RenderOptions = CreateIblRenderParams(),
                    };
                    if (prefilter)
                        material.SetFloat(0, count <= 1 ? 0.0f : (float)mip / (count - 1));
                    XRQuadFrameBuffer target = new(material)
                    {
                        Name = $"LightProbe.{(prefilter ? "Prefilter" : "Irradiance")}.Mip{mip}",
                    };
                    created[createdCount++] = target;
                    target.FullScreenMesh.Name = target.Name;
                    ConfigureProbeFullscreenFramebuffer(target);
                }
                targets = created;
            }
            catch
            {
                for (int index = 0; index < createdCount; index++)
                    created[index].Destroy();
                throw;
            }
        }

        // A generation cannot be replaced until its writer receipt settles.
        // Each deferred request therefore retains a stable attachment tuple and
        // roughness material for the entire lifetime of that generation.
        for (int mip = 0; mip < count; mip++)
            targets[mip].SetRenderTargets((output, EFrameBufferAttachment.ColorAttachment0, mip, -1));
        return targets;
    }
}
