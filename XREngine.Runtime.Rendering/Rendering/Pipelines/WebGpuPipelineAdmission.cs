using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

/// <summary>Shared operation-level WebGPU admission, independent of pipeline asset classes.</summary>
public static class WebGpuPipelineAdmission
{
    public static string? GetOperationRejection(string operation) => operation switch
    {
        "raster-state" or "attachment-clear" or "framebuffer" or "render-area" or
        "cpu-direct-meshes" or "fullscreen-quad" or "screen-ui" or "debug-shapes" or
        "compute" or "color-resolve" or "program-bindings" or "material-override" or
        "memory-barriers" or "gpu-driven-meshes" or "integer-color-targets" or "storage-images" or
        "advanced-stage-execution" or "gpu-meshlet-meshes" or "native-authored-decals" => null,
        "stencil" => "Stencil operations have no installed WebGPU route.",
        "depth-resolve" => "Depth/stencil multisample resolve has no installed WebGPU route.",
        "framebuffer-blit-empty" => "A framebuffer blit must select a supported color resolve; an empty blit has no WebGPU route.",
        "color-resolve-framebuffer-identity" => "Color resolve requires two named, distinct engine framebuffers.",
        "color-resolve-read-buffer" => "Color resolve requires a read attachment from ColorAttachment0 through ColorAttachment7.",
        "color-resolve-linear-filter" => "Filtered color blits have no WebGPU route; color resolve requires nearest filtering.",
        _ => $"Operation '{operation}' has no declared WebGPU capability. Declare the command's actual operations and dependencies.",
    };

    public static void Validate(RenderPipelineRequirements requirements, RenderPipeline pipeline,
        IShaderProgramArtifactResolver? resolver = null, IRuntimeRendererHost? renderer = null)
    {
        if (requirements.Diagnostics.Count != 0)
            throw new NotSupportedException($"WebGPU.Pipeline.RequirementUnsupported: {requirements.Diagnostics[0]}");
        if (GetOutputProfileRejection(requirements) is { } outputReason)
            throw new NotSupportedException($"WebGPU.Pipeline.OutputProfileUnsupported: {outputReason}");
        foreach (string operation in requirements.Operations)
            if (GetOperationRejection(operation) is { } reason)
                throw new NotSupportedException($"WebGPU.Pipeline.OperationUnsupported: {reason}");
        if (renderer is not null && requirements.Operations.Contains("advanced-stage-execution"))
        {
            AdvancedVisibilityFamilyAdmission admission = renderer.GetAdvancedVisibilityFamilyAdmission();
            if (!admission.IsAdmitted)
                throw new NotSupportedException($"WebGPU.Pipeline.NativeFamilyUnavailable: {admission.Reason}");
        }
        if (renderer is not null && requirements.Operations.Contains("gpu-meshlet-meshes"))
        {
            if (renderer is not IAuthoredIndexedBackendCapability meshlets)
                throw new NotSupportedException("WebGPU.Pipeline.MeshletFamilyUnavailable: the selected renderer requires its complete scoped compute meshlet family.");
            if (meshlets.GetMeshletIndexedAdmission(out string meshletReason) == EAuthoredIndexedSubmissionStatus.Rejected)
                throw new NotSupportedException($"WebGPU.Pipeline.MeshletFamilyUnavailable: {meshletReason}");
        }
        if (renderer is not null && requirements.Programs.ContainsKey("indirect::cull-primitive"))
        {
            if (renderer is not IAuthoredIndexedBackendCapability indexed)
                throw new NotSupportedException("WebGPU.Pipeline.IndirectFamilyUnavailable: the selected renderer requires the authored indexed-indirect family.");
            if (indexed.GetIndirectIndexedAdmission(out string indirectReason) == EAuthoredIndexedSubmissionStatus.Rejected)
                throw new NotSupportedException($"WebGPU.Pipeline.IndirectFamilyUnavailable: {indirectReason}");
        }
        foreach ((string pass, string? identity) in requirements.Programs)
        {
            if (!pipeline.TryGetWebPipelineArtifact(pass, out var artifact) || identity is not null && artifact.Identity != identity)
                throw new NotSupportedException($"WebGPU.Pipeline.ArtifactMissing: pass '{pass}' requires its exact declared cooked program.");
            bool wrongRaster = requirements.RasterPrograms.Contains(pass) &&
                !WebPipelineArtifactCatalog.IsCompleteRasterProgram(artifact);
            bool wrongCompute = requirements.ComputePrograms.Contains(pass) &&
                !WebPipelineArtifactCatalog.IsCompleteComputeProgram(artifact);
            if (wrongRaster || wrongCompute)
                throw new NotSupportedException($"WebGPU.Pipeline.ProgramShapeMismatch: pass '{pass}' does not match its declared raster or compute stage.");
        }
        foreach (string identity in requirements.ProgramIdentities)
        {
            if (resolver is null || !resolver.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact) || artifact is null || artifact.Identity != identity)
                throw new NotSupportedException($"WebGPU.Pipeline.ProgramMissing: exact descriptor '{identity}' is unavailable in the owning output catalog.");
            VerifyProgram(artifact);
        }
        foreach (XRRenderProgram program in requirements.RenderPrograms)
        {
            if (!program.TryGetCookedArtifact(ShaderCompileTarget.WebGPUWgsl, resolver, out ShaderProgramArtifact? artifact))
                throw new NotSupportedException($"WebGPU.Pipeline.ProgramMissing: program '{program.Name}' requires its exact cooked companion.");
            VerifyProgram(artifact);
            if (requirements.ComputeRenderPrograms.Contains(program) && !WebPipelineArtifactCatalog.IsCompleteComputeProgram(artifact))
                throw new NotSupportedException($"WebGPU.Pipeline.ProgramShapeMismatch: program '{program.Name}' requires a complete compute stage.");
        }
    }

    /// <summary>Admits a selected output against its graph's declaration rather than its implementation type.</summary>
    public static string? GetOutputProfileRejection(RenderPipelineRequirements requirements)
    {
        RenderPipelineResourceProfile profile = requirements.OutputProfile;
        if (profile.Stereo || profile.ViewCount != 1)
            return "Stereo or multiview output requires a browser XR service that is not installed.";
        if (profile.OutputHDR || profile.OutputColorFormat != EPixelInternalFormat.Rgba8)
            return "Canvas presentation requires an SDR RGBA8 output; internal HDR targets remain available.";
        if (!requirements.SupportedAntiAliasingModes.Contains(profile.AntiAliasingMode))
            return $"Selected anti-aliasing operation '{profile.AntiAliasingMode}' is not declared by this pipeline's output graph.";
        if (profile.AntiAliasingMode == EAntiAliasingMode.Msaa && profile.MsaaSampleCount is not (1 or 4))
            return $"Selected MSAA sample count '{profile.MsaaSampleCount}' has no WebGPU attachment profile; supported sample counts are 1 and 4.";
        if (profile.AntiAliasingMode == EAntiAliasingMode.Dlaa)
            return "Selected DLAA operation requires a native NVIDIA vendor reconstruction service that is unavailable in WebGPU.";
        return null;
    }

    private static void VerifyProgram(ShaderProgramArtifact artifact)
    {
        if (artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.DescriptorBytes.IsDefaultOrEmpty ||
            ShaderProgramArtifactReader.Read(artifact.DescriptorBytes.AsSpan(), artifact.Artifact.Bytes).Identity != artifact.Identity)
            throw new InvalidDataException($"WebGPU.Pipeline.ProgramInvalid: descriptor '{artifact.Identity}' is not a verified WebGPU program.");
        WebPipelineArtifactCatalog.ValidateEngineBindings(artifact);
    }

    /// <summary>Checks material raster state without mutating the authored selection.</summary>
    public static string? GetRasterStateRejection(RenderingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.StencilTest.IsEnabled || parameters.AlphaToCoverage == ERenderParamUsage.Enabled ||
            parameters.BlendModesPerDrawBuffer is { Count: > 0 }
            ? "Stencil, multisample coverage and per-target blending are not admitted by the canvas profile."
            : null;
    }
}
