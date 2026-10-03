using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMaterial
{
    private UberOutlineSurfaceBinding? _uberOutline;

    internal void ValidateUberOutlineSourceForDraw()
    {
        if (Data.EngineSemantic != EngineMaterialSemanticIdentity.UberOutlineV1) return;
        if (_uberOutline is not { } binding)
            throw new NotSupportedException("WebGPU.Material.OutlineBindingMissing: the modeled outline source has not been admitted.");
        if (!binding.TryValidate(out string? reason))
            throw new NotSupportedException(reason);
    }

    private ShaderProgramArtifact ResolveUberOutlineArtifact()
    {
        XRMaterial source = Data.UberOutlineSourceMaterial
            ?? throw new NotSupportedException("WebGPU.Material.OutlineSourceMissing: the cooked outline must retain its exact source material.");
        if (source.CookedOutlineProfile is not { } profile ||
            !source.PassSet.TryGetPass(EMaterialPassIdentity.Outline, out MaterialPassDefinition pass) ||
            !pass.Enabled || pass.ShaderBehavior != EngineMaterialSemanticIdentity.UberOutlineV1)
            throw new NotSupportedException("WebGPU.Material.OutlineProfileMissing: the source must retain the admitted outline pass profile.");
        if (!UberOutlineSurfaceBinding.TryCreate(source, profile, out UberOutlineSurfaceBinding? binding, out string? reason))
            throw new NotSupportedException(reason);
        if (Data.Shaders.Count != 1 || Data.Shaders[0].Type != EShaderType.Fragment ||
            !Data.Shaders[0].TryGetCookedArtifact(ShaderCompileTarget.WebGPUWgsl, Renderer.ShaderArtifacts, out ShaderProgramArtifact? artifact) ||
            artifact.Identity != pass.CookedArtifactIdentity)
            throw new NotSupportedException("WebGPU.Material.OutlineCompanionMissing: the owned variant requires the source pass's exact complete cooked descriptor.");
        UberOutlineProgramContract.Validate(artifact, binding!.Features);
        SetField(ref _uberOutline, binding);
        return artifact;
    }

    private void PublishUberOutline()
    {
        UberOutlineSurfaceBinding binding = _uberOutline
            ?? throw new NotSupportedException("WebGPU.Material.OutlineBindingMissing: the modeled outline source has not been admitted.");
        if (!binding.TryValidate(out string? reason))
            throw new NotSupportedException(reason);
        RenderFrameViewSelection view = Renderer.RequireFrozenView();
        Program.SetVector4("u_ScreenParams", GetUberOutlineScreenParameters(in view));
        Program.Data.Uniform("u_Time", binding.RenderTimeEnabled ? view.ElapsedTime : 0.0f);
        Program.Data.Uniform("OutlineFeatures", binding.Features);
    }

    internal static Vector4 GetUberOutlineScreenParameters(in RenderFrameViewSelection view)
    {
        // Match XRCameraParameters.SetUniforms, including the orthographic
        // override that supplies authored view width/height rather than pixels.
        float width = view.ScreenSize.X;
        float height = view.ScreenSize.Y;
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0 ||
            !float.IsFinite(1 / width) || !float.IsFinite(1 / height))
            throw new NotSupportedException("WebGPU.Material.OutlineViewportInvalid: screen-space outline expansion requires positive finite dimensions and finite reciprocals.");
        return new Vector4(width, height, 1 + 1 / width, 1 + 1 / height);
    }
}
