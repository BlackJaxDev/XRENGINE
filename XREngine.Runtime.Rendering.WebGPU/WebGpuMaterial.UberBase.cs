using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMaterial
{
    private UberBaseSurfaceBinding? _uberBase;
    private string? _uberBaseReceiverIdentity;

    private ShaderProgramArtifact ResolveUberBaseArtifact()
    {
        XRMaterial source = Data.StandardLitColorSourceMaterial ?? Data;
        UberBaseMaterialProfile profile = source.CookedUberBaseProfile
            ?? throw new NotSupportedException($"WebGPU.UberBase.ProfileMissing: '{source.Name}' requires its exact target-cooked canonical Uber profile.");
        IShaderProgramArtifactResolver resolver = Renderer.ShaderArtifacts
            ?? throw new NotSupportedException("WebGPU.UberBase.ArtifactCatalogMissing: the renderer requires the exact prepared shader catalog.");
        if (!resolver.TryResolve(profile.CookedArtifactIdentity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? receiver) ||
            receiver is null || !EngineUberBaseMaterialAdmission.TryAdmit(source, receiver, out UberBaseSurfaceBinding? binding, out string? reason))
            throw new NotSupportedException($"WebGPU.UberBase.ArtifactMissing: '{source.Name}' requires prepared variant {profile.Variant.VariantHash:x16} and source {profile.SourceIdentity}.");
        SetField(ref _uberBase, binding);
        SetField(ref _uberBaseReceiverIdentity, receiver.Identity);
        SetField(ref _litAuxiliaryPass, Data.StandardLitColorAuxiliaryPass);
        if (_litAuxiliaryPass == EStandardLitColorAuxiliaryPass.None)
        {
            bool shadows = (profile.PipelineFlags & 3u) == 3u;
            SetField(ref _directionalShadowReceiver, shadows);
            SetField(ref _localShadowReceiver, shadows);
            return receiver;
        }
        string pass = _litAuxiliaryPass switch
        {
            EStandardLitColorAuxiliaryPass.DepthNormal => "depth-normal", EStandardLitColorAuxiliaryPass.ShadowDepth => "depth",
            EStandardLitColorAuxiliaryPass.PointShadowDepth => "point-shadow-depth", EStandardLitColorAuxiliaryPass.SpotShadowDepth => "spot-shadow-depth",
            _ => throw new NotSupportedException("WebGPU.UberBase.AuxiliaryPassUnsupported: the requested Uber pass has no admitted lowering."),
        };
        if (source.GetEffectiveTransparencyMode() is not (ETransparencyMode.Opaque or ETransparencyMode.Masked))
            throw new NotSupportedException("WebGPU.UberBase.AuxiliaryCoverageUnsupported: sorted transparent sources cannot enter depth or shadow replay.");
        ShaderProgramArtifact? artifact = null;
        foreach (UberBasePassArtifact companion in profile.PassArtifacts)
            if (companion.Pass == pass)
            {
                if (artifact is not null || !resolver.TryResolve(companion.ArtifactIdentity, ShaderCompileTarget.WebGPUWgsl, out artifact))
                    throw new NotSupportedException($"WebGPU.UberBase.AuxiliaryArtifactMissing: '{source.Name}' requires its unique prepared '{pass}' companion.");
            }
        if (artifact is null || !EngineUberBaseMaterialAdmission.TryValidateArtifact(source.ID, profile, artifact, pass, out reason))
            throw new NotSupportedException($"WebGPU.UberBase.AuxiliaryArtifactMissing: '{source.Name}', pass '{pass}', prepared variant {profile.Variant.VariantHash:x16}: {reason}");
        return artifact;
    }

    private bool TryPublishUberBase()
    {
        if (_uberBase is not { } binding) return false;
        XRMaterial source = Data.StandardLitColorSourceMaterial ?? Data;
        if (!ReferenceEquals(source.CookedUberBaseProfile, binding.Profile) || binding.Profile.CookedArtifactIdentity != _uberBaseReceiverIdentity ||
            !binding.TryGetValues(out ReadOnlySpan<byte> values, out string? reason))
            throw new NotSupportedException("WebGPU.UberBase.SourceChanged: the prepared source profile changed; replace the material after an exact recook.");
        WebGpuFrameBuffer? target = Renderer.GetBoundEngineFrameBuffer();
        if (target is null || !target.HasDepth || target.SampleCount is not (1 or 4))
            throw new NotSupportedException("WebGPU.UberBase.OutputUnsupported: the generic Uber raster profile requires a one- or four-sample depth attachment.");
        Program.PublishUberBaseParameters(values);
        Program.Data.Uniform("RenderTime", (binding.Features & UberBaseMaterialProfile.RenderTime) != 0 ? Renderer.RequireFrozenView().ElapsedTime : 0.0f);
        for (int role = 0; role < binding.Textures.Length; role++)
        {
            UberBaseTextureBinding? texture = binding.Textures[role];
            if (texture is null || _litAuxiliaryPass != EStandardLitColorAuxiliaryPass.None && role is not (0 or 2)) continue;
            Program.Data.Sampler(texture.SamplerName, texture.Texture, texture.SourceTextureSlot);
        }
        if (_litAuxiliaryPass is EStandardLitColorAuxiliaryPass.PointShadowDepth or EStandardLitColorAuxiliaryPass.SpotShadowDepth)
        { RequirePointShadowOutput(target); return true; }
        if (_litAuxiliaryPass == EStandardLitColorAuxiliaryPass.ShadowDepth)
        {
            if (target.HasColor || target.SampleCount != 1) throw new NotSupportedException("WebGPU.UberBase.ShadowOutputUnsupported: directional Uber replay requires single-sample depth-only output.");
            return true;
        }
        if (target.ColorFormats.Length != 1 || target.ColorFormats[0] != "rgba16float")
            throw new NotSupportedException("WebGPU.UberBase.OutputUnsupported: Uber color and geometric normal replay require one RGBA16F attachment.");
        if (_litAuxiliaryPass == EStandardLitColorAuxiliaryPass.None)
        {
            Renderer.PublishUberBaseLighting(Program, binding.Profile.PipelineFlags);
        }
        return true;
    }
}
