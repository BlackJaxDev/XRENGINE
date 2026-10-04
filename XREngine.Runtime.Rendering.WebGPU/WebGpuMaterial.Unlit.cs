using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMaterial
{
    private EngineUnlitSurfaceBinding? _unlitSurface;
    private string? _unlitSourceIdentity;
    private EngineMaterialSemanticIdentity _unlitSemantic;

    private ShaderProgramArtifact ResolveUnlitArtifact()
    {
        XRMaterial source = Data.StandardLitColorSourceMaterial ?? Data;
        ShaderProgramArtifact? receiver = null;
        EngineUnlitSurfaceBinding? binding = null;
        string? reason = null;
        if (source.Shaders.Count == 0)
        {
            EngineMaterialVariantKey builtInKey = EngineUnlitMaterialShaderGenerator.BuiltInKey(source.EngineSemantic);
            if (Renderer.MaterialVariants?.TryResolve(builtInKey, out receiver) != true || receiver is null ||
                !EngineUnlitMaterialAdmission.TryAdmitBuiltIn(source, receiver, builtInKey, out binding, out reason))
                throw new NotSupportedException($"WebGPU.Unlit.BuiltInUnsupported: '{source.Name}' requires its exact declared '{builtInKey}' variant: {reason}");
        }
        else
        {
            receiver = null;
            foreach (XRShader shader in source.Shaders)
            {
                if (!shader.TryGetCookedArtifact(ShaderCompileTarget.WebGPUWgsl, Renderer.ShaderArtifacts,
                        out ShaderProgramArtifact? stage) || receiver is not null && receiver.Identity != stage.Identity)
                    throw new NotSupportedException($"WebGPU.Unlit.CompanionMissing: '{source.Name}' requires its exact whole-program source descriptor.");
                receiver = stage;
            }
            if (receiver is null || receiver.DescriptorBytes.IsDefaultOrEmpty ||
                !EngineUnlitMaterialAdmission.TryAdmit(source, receiver, out binding, out reason))
                throw new NotSupportedException($"WebGPU.Unlit.SurfaceUnsupported: '{source.Name}': {reason}");
        }
        if (binding is null || !binding.TryRead(out EngineUnlitSurface surface, out reason))
            throw new NotSupportedException($"WebGPU.Unlit.SurfaceUnsupported: '{source.Name}': {reason}");
        SetField(ref _unlitSurface, binding);
        SetField(ref _unlitSourceIdentity, receiver.Identity);
        SetField(ref _unlitSemantic, surface.Semantic);
        EStandardLitColorAuxiliaryPass pass = Data.StandardLitColorAuxiliaryPass;
        SetField(ref _litAuxiliaryPass, pass);
        if (pass == EStandardLitColorAuxiliaryPass.None)
            return receiver;
        if (source.IsTransparentLike() || Data.Shaders.Count != 0 ||
            pass is not (EStandardLitColorAuxiliaryPass.DepthNormal or EStandardLitColorAuxiliaryPass.ShadowDepth or
                EStandardLitColorAuxiliaryPass.PointShadowDepth or EStandardLitColorAuxiliaryPass.SpotShadowDepth) ||
            pass != EStandardLitColorAuxiliaryPass.DepthNormal &&
                surface.Semantic != EngineMaterialSemanticIdentity.UnlitAlphaTextureV4)
            throw new NotSupportedException("WebGPU.Unlit.AuxiliaryUnsupported: exact opaque or masked source-owned replay is required.");
        string passName = pass switch
        {
            EStandardLitColorAuxiliaryPass.DepthNormal => "depth-normal",
            EStandardLitColorAuxiliaryPass.ShadowDepth => "depth",
            EStandardLitColorAuxiliaryPass.PointShadowDepth => "point-shadow-depth",
            _ => "spot-shadow-depth",
        };
        EngineMaterialVariantKey key = EngineUnlitMaterialShaderGenerator.CompanionKey(surface.Semantic, passName);
        if (Renderer.MaterialVariants?.TryResolve(key, out ShaderProgramArtifact? artifact) != true || artifact is null ||
            !EngineUnlitShaderProvenance.TryValidateCompanion(artifact, key, out reason))
            throw new NotSupportedException($"WebGPU.Unlit.CompanionUnsupported: '{source.Name}' requires '{key}': {reason}");
        return artifact;
    }

    private bool TryPublishUnlit()
    {
        if (_unlitSurface is null) return false;
        if (!_unlitSurface.TryRead(out EngineUnlitSurface surface, out string? reason) || surface.Semantic != _unlitSemantic)
            throw new NotSupportedException($"WebGPU.Unlit.SourceChanged: '{Data.Name}': {reason} Replace the material at a resource-generation boundary.");
        XRMaterial source = Data.StandardLitColorSourceMaterial ?? Data;
        if (source.Shaders.Count == 0)
        {
            EngineMaterialVariantKey key = EngineUnlitMaterialShaderGenerator.BuiltInKey(source.EngineSemantic);
            if (Renderer.MaterialVariants?.TryResolve(key, out ShaderProgramArtifact? builtIn) != true ||
                builtIn?.Identity != _unlitSourceIdentity)
                throw new NotSupportedException("WebGPU.Unlit.ProgramChanged: replace the material at a resource-generation boundary.");
        }
        else if (source.Shaders.Count != 1 || source.Shaders[0].CookedArtifactIdentity != _unlitSourceIdentity)
            throw new NotSupportedException("WebGPU.Unlit.ProgramChanged: replace the material at a resource-generation boundary.");
        WebGpuFrameBuffer? target = Renderer.GetBoundEngineFrameBuffer();
        if (target is null || !target.HasDepth || target.SampleCount is not (1 or 4))
            throw new NotSupportedException("WebGPU.Unlit.OutputUnsupported: unlit raster replay requires one or four matching color/depth samples.");
        ValidateCoverageRasterState(surface.TransparencyMode);
        if (surface.Semantic == EngineMaterialSemanticIdentity.UnlitColorV1)
            Program.SetVector4("MatColor", surface.Color);
        else
            Program.Data.Sampler("Texture0", surface.Texture!, 0);
        if (surface.Semantic == EngineMaterialSemanticIdentity.UnlitAlphaTextureV4)
            Program.SetVector4("UnlitAlphaControls", new Vector4(surface.AlphaCutoff, 0, 0, 0));
        if (_litAuxiliaryPass is EStandardLitColorAuxiliaryPass.PointShadowDepth or EStandardLitColorAuxiliaryPass.SpotShadowDepth)
        {
            RequirePointShadowOutput(target);
            return true;
        }
        if (_litAuxiliaryPass == EStandardLitColorAuxiliaryPass.ShadowDepth)
        {
            if (target.HasColor || target.SampleCount != 1)
                throw new NotSupportedException("WebGPU.Unlit.ShadowOutputUnsupported: directional replay requires single-sample depth-only output.");
            return true;
        }
        if (target.ColorFormats.Length != 1 || target.ColorFormats[0] != "rgba16float")
            throw new NotSupportedException("WebGPU.Unlit.ColorOutputUnsupported: color and normal replay require one RGBA16F attachment.");
        return true;
    }
}
