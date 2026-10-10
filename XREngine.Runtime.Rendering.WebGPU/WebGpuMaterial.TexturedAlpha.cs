using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMaterial
{
    private TexturedAlphaSurfaceBinding? _texturedAlphaSurface;
    private string? _texturedAlphaSourceIdentity;

    private ShaderProgramArtifact ResolveTexturedAlphaArtifact()
    {
        XRMaterial source = Data.StandardLitColorSourceMaterial ?? Data;
        ShaderProgramArtifact? receiver = null;
        foreach (XRShader shader in source.Shaders)
        {
            if (!shader.TryGetCookedArtifact(ShaderCompileTarget.WebGPUWgsl, Renderer.ShaderArtifacts, out ShaderProgramArtifact? stage) ||
                receiver is not null && stage.Identity != receiver.Identity)
                throw new NotSupportedException($"WebGPU.Material.TexturedAlphaCompanionMissing: '{source.Name}' requires identical exact descriptors on every retained stage.");
            receiver = stage;
        }
        string? reason = null;
        if (receiver is null || receiver.DescriptorBytes.IsDefaultOrEmpty ||
            !EngineTexturedAlphaMaterialAdmission.TryAdmit(source, receiver, out TexturedAlphaSurfaceBinding? binding, out reason))
            throw new NotSupportedException($"WebGPU.Material.TexturedAlphaUnsupported: '{source.Name}': {reason}");
        SetField(ref _texturedAlphaSurface, binding);
        SetField(ref _texturedAlphaSourceIdentity, receiver.Identity);
        EStandardLitColorAuxiliaryPass pass = Data.StandardLitColorAuxiliaryPass;
        SetField(ref _litAuxiliaryPass, pass);
        if (pass == EStandardLitColorAuxiliaryPass.None)
        {
            SetField(ref _directionalShadowReceiver, true);
            SetField(ref _localShadowReceiver, true);
            return receiver;
        }
        if (source.GetEffectiveTransparencyMode() != ETransparencyMode.Masked || Data.Shaders.Count != 0)
            throw new NotSupportedException("WebGPU.Material.TexturedAlphaAuxiliaryUnsupported: exact source-owned masked replay is required.");
        EngineMaterialVariantKey key = new(EngineMaterialSemanticIdentity.AuthoredLitTextureAlphaV1,
            ShaderCompileTarget.WebGPUWgsl,
            pass == EStandardLitColorAuxiliaryPass.DepthNormal ? "depth-normal" :
                pass == EStandardLitColorAuxiliaryPass.ShadowDepth ? "depth" :
                pass == EStandardLitColorAuxiliaryPass.PointShadowDepth ? "point-shadow-depth" : "spot-shadow-depth",
            "position-normal-uv-v1",
            pass == EStandardLitColorAuxiliaryPass.DepthNormal ? "normal-rgba16f-v1" :
                pass == EStandardLitColorAuxiliaryPass.ShadowDepth ? "depth-normal-v1" :
                pass == EStandardLitColorAuxiliaryPass.PointShadowDepth ? "radial-r16f-v1" : "projected-r16f-v1");
        if (Renderer.MaterialVariants?.TryResolve(key, out ShaderProgramArtifact? artifact) != true || artifact is null)
            throw new NotSupportedException($"WebGPU.Material.TexturedAlphaVariantMissing: '{source.Name}' requires '{key}'.");
        if (!EngineTexturedAlphaMaterialAdmission.TryValidateCompanion(artifact, key, out reason))
            throw new NotSupportedException($"WebGPU.Material.TexturedAlphaCompanionUnsupported: '{source.Name}': {reason}");
        return artifact;
    }

    private bool TryPublishTexturedAlpha()
    {
        if (_texturedAlphaSurface is null) return false;
        if (!_texturedAlphaSurface.TryRead(out TexturedAlphaSurface surface, out string? reason))
            throw new NotSupportedException($"WebGPU.Material.TexturedAlphaSurfaceChanged: '{Data.Name}': {reason}");
        XRMaterial source = Data.StandardLitColorSourceMaterial ?? Data;
        foreach (XRShader shader in source.Shaders)
            if (shader.CookedArtifactIdentity != _texturedAlphaSourceIdentity)
                throw new NotSupportedException("WebGPU.Material.TexturedAlphaProgramChanged: replace the material at a resource-generation boundary.");
        WebGpuFrameBuffer? target = Renderer.GetBoundEngineFrameBuffer();
        if (target is null || !target.HasDepth || target.SampleCount is not (1 or 4))
            throw new NotSupportedException("WebGPU.Material.TexturedAlphaOutputUnsupported: generic textured-alpha replay requires a one- or four-sample depth attachment.");
        ValidateCoverageRasterState(surface.Values);
        Program.SetVector4("StandardLitBaseColorOpacity", Vector4.One);
        Program.SetVector4("StandardLitRoughnessMetallicSpecularEmission", new(surface.Values.Roughness,
            surface.Values.Metallic, surface.Values.Specular, surface.Values.Emission));
        // The canonical shader discards below cutoff in both masked and blended color passes.
        Program.SetVector4("StandardLitCoverage", new(1, surface.Values.AlphaCutoff, 0, 0));
        Program.Data.Sampler("TexturedAlphaBaseColorTexture", surface.BaseColor.Texture, 0);
        Program.Data.Sampler("TexturedAlphaOpacityTexture", surface.Opacity.Texture, 1);
        if (_litAuxiliaryPass is EStandardLitColorAuxiliaryPass.PointShadowDepth or EStandardLitColorAuxiliaryPass.SpotShadowDepth)
        {
            RequirePointShadowOutput(target);
            return true;
        }
        if (_litAuxiliaryPass == EStandardLitColorAuxiliaryPass.ShadowDepth)
        {
            if (target.HasColor || target.SampleCount != 1)
                throw new NotSupportedException("WebGPU.Material.TexturedAlphaShadowOutputUnsupported: directional replay requires single-sample depth-only output.");
            return true;
        }
        if (target.ColorFormats.Length != 1 || target.ColorFormats[0] != "rgba16float")
            throw new NotSupportedException("WebGPU.Material.TexturedAlphaOutputUnsupported: color and normal replay require one RGBA16F attachment.");
        if (_litAuxiliaryPass == EStandardLitColorAuxiliaryPass.None)
        {
            Renderer.PublishForwardLights(Program, true, true);
            Renderer.PublishAmbientOcclusion(Program);
        }
        return true;
    }
}
