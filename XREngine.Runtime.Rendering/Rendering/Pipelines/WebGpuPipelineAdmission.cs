using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

/// <summary>Shared operation-level WebGPU admission, independent of pipeline asset classes.</summary>
public static class WebGpuPipelineAdmission
{
    public static string? GetOperationRejection(string operation) => operation switch
    {
        "raster-state" or "attachment-clear" or "framebuffer" or "render-area" or
        "cpu-direct-meshes" or "fullscreen-quad" or "screen-ui" or "debug-shapes" or
        "compute" or "color-resolve" or "program-bindings" or "material-override" => null,
        "integer-color-targets" => "Integer color attachments required for visibility records have no installed WebGPU framebuffer format route.",
        "memory-barriers" => "Explicit engine memory-barrier synchronization has no installed WebGPU operation.",
        "advanced-stage-execution" => "VPRC_AdvancedRenderStage requires its native visibility/shading stage executor and cooked shader family; that WebGPU executor is not installed.",
        "gpu-driven-meshes" => "GPU-driven engine mesh submission has no installed WebGPU operation.",
        "storage-images" => "Storage-image binding has no installed WebGPU operation.",
        "stencil" => "Stencil operations have no installed WebGPU route.",
        "depth-resolve" => "Depth/stencil multisample resolve has no installed WebGPU route.",
        _ => $"Operation '{operation}' has no declared WebGPU capability. Declare the command's actual operations and dependencies.",
    };

    public static void Validate(RenderPipelineRequirements requirements, RenderPipeline pipeline,
        IShaderProgramArtifactResolver? resolver = null)
    {
        if (requirements.Diagnostics.Count != 0)
            throw new NotSupportedException($"WebGPU.Pipeline.RequirementUnsupported: {requirements.Diagnostics[0]}");
        foreach (string operation in requirements.Operations)
            if (GetOperationRejection(operation) is { } reason)
                throw new NotSupportedException($"WebGPU.Pipeline.OperationUnsupported: {reason}");
        foreach ((string pass, string? identity) in requirements.Programs)
            if (!pipeline.TryGetWebPipelineArtifact(pass, out var artifact) || identity is not null && artifact.Identity != identity)
                throw new NotSupportedException($"WebGPU.Pipeline.ArtifactMissing: pass '{pass}' requires its exact declared cooked program.");
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
        }
    }

    private static void VerifyProgram(ShaderProgramArtifact artifact)
    {
        if (artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.DescriptorBytes.IsDefaultOrEmpty ||
            ShaderProgramArtifactReader.Read(artifact.DescriptorBytes.AsSpan(), artifact.Artifact.Bytes).Identity != artifact.Identity)
            throw new InvalidDataException($"WebGPU.Pipeline.ProgramInvalid: descriptor '{artifact.Identity}' is not a verified WebGPU program.");
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
