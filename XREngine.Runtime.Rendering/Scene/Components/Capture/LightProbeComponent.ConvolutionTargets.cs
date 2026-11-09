using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Capture.Lights;

public partial class LightProbeComponent
{
    private XRQuadFrameBuffer[] _irradianceMipTargets = [];
    private XRQuadFrameBuffer[] _prefilterMipTargets = [];
    private int _prefilterMipSourceDimension;

    private void DestroyIblMipTargets()
    {
        foreach (XRQuadFrameBuffer target in _irradianceMipTargets)
            DestroyIblMipTarget(target);
        foreach (XRQuadFrameBuffer target in _prefilterMipTargets)
            DestroyIblMipTarget(target);
        _irradianceMipTargets = [];
        _prefilterMipTargets = [];
        _prefilterMipSourceDimension = 0;
    }

    private static void DestroyIblMipTarget(XRQuadFrameBuffer target)
    {
        // Each mip owns its material. The quad treats materials as borrowed.
        XRMaterial? material = target.Material;
        target.Destroy();
        material?.Destroy();
    }

    private XRQuadFrameBuffer[] GetIblMipTargets(bool prefilter, XRTexture2D output)
    {
        ref XRQuadFrameBuffer[] targets = ref (prefilter ? ref _prefilterMipTargets : ref _irradianceMipTargets);
        int count = output.SmallestMipmapLevel + 1;
        XRQuadFrameBuffer template = (prefilter ? _prefilterFBO : _irradianceFBO)
            ?? throw new InvalidOperationException("Convolution template is missing.");
        XRTexture source = (prefilter ? _prefilterSourceTexture : _irradianceSourceTexture)
            ?? throw new InvalidOperationException("Convolution source is missing.");
        XRMaterial templateMaterial = template.Material
            ?? throw new InvalidOperationException("Convolution template material is missing.");
        if (!AreIblMipTargetsCompatible(targets, count, templateMaterial, source) ||
            prefilter && _prefilterMipSourceDimension != _prefilterSourceDimension)
        {
            foreach (XRQuadFrameBuffer target in targets)
                DestroyIblMipTarget(target);
            targets = [];
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
                if (prefilter)
                    _prefilterMipSourceDimension = _prefilterSourceDimension;
            }
            catch
            {
                for (int index = 0; index < createdCount; index++)
                    DestroyIblMipTarget(created[index]);
                throw;
            }
        }

        // A generation cannot be replaced until its writer receipt settles.
        // The next generation can then bind its new source and output to these
        // compatible materials and targets.
        for (int mip = 0; mip < count; mip++)
        {
            XRMaterial material = targets[mip].Material!;
            if (!ReferenceEquals(material.Textures[0], source))
                material.Textures[0] = source;
            // Keep the attachment array when this output and mip remain bound.
            if (targets[mip].Targets is not { Length: 1 } attachments ||
                !ReferenceEquals(attachments[0].Target, output) ||
                attachments[0].Attachment != EFrameBufferAttachment.ColorAttachment0 ||
                attachments[0].MipLevel != mip || attachments[0].LayerIndex != -1)
            {
                targets[mip].SetRenderTargets((output, EFrameBufferAttachment.ColorAttachment0, mip, -1));
            }
        }
        return targets;
    }

    private static bool AreIblMipTargetsCompatible(
        XRQuadFrameBuffer[] targets,
        int count,
        XRMaterial templateMaterial,
        XRTexture source)
    {
        if (targets.Length != count)
            return false;

        for (int mip = 0; mip < targets.Length; mip++)
        {
            XRMaterial? material = targets[mip].Material;
            if (material is null || material.Textures.Count != 1 ||
                material.Textures[0]?.GetType() != source.GetType() ||
                material.Shaders.Count != templateMaterial.Shaders.Count)
                return false;
            for (int shader = 0; shader < material.Shaders.Count; shader++)
                if (!ReferenceEquals(material.Shaders[shader], templateMaterial.Shaders[shader]))
                    return false;
        }

        return true;
    }
}
