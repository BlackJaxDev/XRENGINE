using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMaterial
{
    private AuthoredTexturedSurfaceBinding? _authoredTexturedSurface;
    private string? _authoredTexturedSourceIdentity;
    private int _authoredTexturedFlags;

    private ShaderProgramArtifact ResolveAuthoredTexturedArtifact()
    {
        XRMaterial source = Data.StandardLitColorSourceMaterial ?? Data;
        ShaderProgramArtifact? receiver = null;
        foreach (XRShader shader in source.Shaders)
        {
            if (!shader.TryGetCookedArtifact(ShaderCompileTarget.WebGPUWgsl, Renderer.ShaderArtifacts, out ShaderProgramArtifact? stage) ||
                receiver is not null && stage.Identity != receiver.Identity)
                throw new NotSupportedException($"WebGPU.Material.AuthoredTexturedCompanionMissing: '{source.Name}' requires identical exact descriptors on every retained stage.");
            receiver = stage;
        }
        string? reason = null;
        if (receiver is null || receiver.DescriptorBytes.IsDefaultOrEmpty ||
            !EngineAuthoredTexturedMaterialAdmission.TryAdmit(source, receiver, out AuthoredTexturedSurfaceBinding? binding, out reason))
            throw new NotSupportedException($"WebGPU.Material.AuthoredTexturedUnsupported: '{source.Name}': {reason}");
        if (!binding!.TryRead(out AuthoredTexturedSurface surface, out reason))
            throw new NotSupportedException($"WebGPU.Material.AuthoredTexturedUnsupported: {reason}");
        SetField(ref _authoredTexturedSurface, binding);
        SetField(ref _authoredTexturedFlags, surface.TextureFlags);
        SetField(ref _authoredTexturedSourceIdentity, receiver.Identity);
        EStandardLitColorAuxiliaryPass pass = Data.StandardLitColorAuxiliaryPass;
        SetField(ref _litAuxiliaryPass, pass);
        if (pass == EStandardLitColorAuxiliaryPass.None)
        {
            SetField(ref _directionalShadowReceiver, true);
            SetField(ref _localShadowReceiver, true);
            return receiver;
        }
        if (source.GetEffectiveTransparencyMode() is not (ETransparencyMode.Opaque or ETransparencyMode.Masked) || Data.Shaders.Count != 0 ||
            pass is not (EStandardLitColorAuxiliaryPass.DepthNormal or EStandardLitColorAuxiliaryPass.ShadowDepth or
                EStandardLitColorAuxiliaryPass.PointShadowDepth or EStandardLitColorAuxiliaryPass.SpotShadowDepth))
            throw new NotSupportedException("WebGPU.Material.AuthoredTexturedAuxiliaryUnsupported: exact source-owned opaque or masked replay is required.");
        EngineMaterialVariantKey key = EngineAuthoredTexturedShaderGenerator.CompanionKey(surface.TextureFlags,
            pass == EStandardLitColorAuxiliaryPass.DepthNormal ? "depth-normal" :
                pass == EStandardLitColorAuxiliaryPass.ShadowDepth ? "depth" :
                pass == EStandardLitColorAuxiliaryPass.PointShadowDepth ? "point-shadow-depth" : "spot-shadow-depth");
        if (Renderer.MaterialVariants?.TryResolve(key, out ShaderProgramArtifact? artifact) != true || artifact is null)
            throw new NotSupportedException($"WebGPU.Material.AuthoredTexturedVariantMissing: '{source.Name}' requires '{key}'.");
        if (!EngineAuthoredTexturedMaterialAdmission.TryValidateCompanion(artifact, key, out reason))
            throw new NotSupportedException($"WebGPU.Material.AuthoredTexturedCompanionUnsupported: '{source.Name}': {reason}");
        return artifact;
    }

    private bool TryPublishAuthoredTextured()
    {
        if (_authoredTexturedSurface is null) return false;
        if (!_authoredTexturedSurface.TryRead(out AuthoredTexturedSurface surface, out string? reason))
            throw new NotSupportedException($"WebGPU.Material.AuthoredTexturedSurfaceChanged: '{Data.Name}': {reason}");
        if (surface.TextureFlags != _authoredTexturedFlags)
            throw new NotSupportedException("WebGPU.Material.AuthoredTexturedRolesChanged: replace the material at a resource-generation boundary.");
        XRMaterial source = Data.StandardLitColorSourceMaterial ?? Data;
        foreach (XRShader shader in source.Shaders)
            if (shader.CookedArtifactIdentity != _authoredTexturedSourceIdentity)
                throw new NotSupportedException("WebGPU.Material.AuthoredTexturedProgramChanged: replace the material at a resource-generation boundary.");
        WebGpuFrameBuffer? target = Renderer.GetBoundEngineFrameBuffer();
        if (target is null || !target.HasDepth || target.SampleCount is not (1 or 4))
            throw new NotSupportedException("WebGPU.Material.AuthoredTexturedOutputUnsupported: generic authored textured replay requires a one- or four-sample depth attachment.");
        ValidateCoverageRasterState(surface.Values);
        Program.SetVector4("StandardLitBaseColorOpacity", Vector4.One);
        Program.SetVector4("StandardLitRoughnessMetallicSpecularEmission", new(surface.Values.Roughness,
            surface.Values.Metallic, surface.Values.Specular, surface.Values.Emission));
        Program.SetVector4("AuthoredTexturedControls", new(surface.TextureFlags, surface.Values.AlphaCutoff,
            surface.NormalMapMode, surface.HeightMapScale));
        Program.Data.Sampler("AuthoredTexturedBaseColorTexture", surface.BaseColor.Texture, 0);
        Program.Data.Sampler("AuthoredTexturedNormalTexture", (surface.Normal ?? surface.BaseColor).Texture, 1);
        Program.Data.Sampler("AuthoredTexturedSpecularTexture", (surface.Specular ?? surface.BaseColor).Texture, 2);
        Program.Data.Sampler("AuthoredTexturedOpacityTexture", (surface.Opacity ?? surface.BaseColor).Texture, 3);
        if (_litAuxiliaryPass is EStandardLitColorAuxiliaryPass.PointShadowDepth or EStandardLitColorAuxiliaryPass.SpotShadowDepth)
        {
            RequirePointShadowOutput(target);
            return true;
        }
        if (_litAuxiliaryPass == EStandardLitColorAuxiliaryPass.ShadowDepth)
        {
            if (target.HasColor || target.SampleCount != 1)
                throw new NotSupportedException("WebGPU.Material.AuthoredTexturedShadowOutputUnsupported: directional replay requires single-sample depth-only output.");
            return true;
        }
        if (target.ColorFormats.Length != 1 || target.ColorFormats[0] != "rgba16float")
            throw new NotSupportedException("WebGPU.Material.AuthoredTexturedOutputUnsupported: color and normal replay require one RGBA16F attachment.");
        if (_litAuxiliaryPass == EStandardLitColorAuxiliaryPass.None)
        {
            Renderer.PublishForwardLights(Program, true, true);
            Renderer.PublishAmbientOcclusion(Program);
        }
        return true;
    }
}
