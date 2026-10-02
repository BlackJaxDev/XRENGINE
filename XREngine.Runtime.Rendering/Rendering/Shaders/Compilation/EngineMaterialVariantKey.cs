using XREngine.Rendering;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Complete selection key for one cooked material shader. Pass and profile identifiers
/// are explicit producer/consumer contracts, not names inferred from authored GLSL.
/// </summary>
public readonly record struct EngineMaterialVariantKey(
    EngineMaterialSemanticIdentity Semantic,
    ShaderCompileTarget Target,
    string Pass,
    string VertexProfile,
    string OutputProfile)
{
    public void Validate()
    {
        Semantic.Validate();
        if (Semantic.Semantic == EngineMaterialSemantic.None)
            throw new ArgumentException("A cooked material variant requires a non-empty engine semantic.");
        if (Semantic == EngineMaterialSemanticIdentity.AuthoredLitV1)
            throw new ArgumentException("Authored lit materials require exact per-stage cooked companions, not a built-in variant selector.");
        if (!Enum.IsDefined(Target))
            throw new ArgumentOutOfRangeException(nameof(Target), Target, "Unsupported shader target.");
        ValidateProfile(Pass, nameof(Pass));
        ValidateProfile(VertexProfile, nameof(VertexProfile));
        ValidateProfile(OutputProfile, nameof(OutputProfile));
        if (Semantic == EngineMaterialSemanticIdentity.StandardLitTextureV1 &&
            (Target != ShaderCompileTarget.WebGPUWgsl ||
             VertexProfile is not ("position-normal-uv-v1" or "position-normal-tangent-uv-v1") ||
             !(Pass == "opaque-forward" && OutputProfile is "linear-hdr-v1" or "linear-hdr-directional-shadow-v1" or "linear-hdr-local-shadows-v1" ||
               Pass == "depth-normal" && VertexProfile == "position-normal-tangent-uv-v1" && OutputProfile == "normal-rgba16f-v1")))
            throw new ArgumentException("StandardLitTextureV1 requires its exact textured forward or depth-normal profile.");
        if (Semantic.IsSkybox() && (Target != ShaderCompileTarget.WebGPUWgsl || Pass != "background" ||
            VertexProfile != "fullscreen-sky-v1" || OutputProfile != "linear-hdr-v1"))
            throw new ArgumentException("Skybox variants require WebGPU background/fullscreen-sky-v1/linear-hdr-v1.");
        if (Semantic == EngineMaterialSemanticIdentity.StandardLitColorV2 &&
            (Target != ShaderCompileTarget.WebGPUWgsl || !IsLitCoverageProfile()))
            throw new ArgumentException("StandardLitColorV2 requires an exact forward-coverage, depth-normal, or depth profile.");
        if (Semantic == EngineMaterialSemanticIdentity.OpaqueShadowDepthV1 &&
            (Target != ShaderCompileTarget.WebGPUWgsl || Pass != "depth" ||
             VertexProfile != "static-position-v1" || OutputProfile != "depth-normal-v1"))
            throw new ArgumentException("OpaqueShadowDepthV1 requires the WebGPU depth/static-position-v1/depth-normal-v1 variant.");
        if (Semantic == EngineMaterialSemanticIdentity.OpaquePointShadowDepthV1 &&
            (Target != ShaderCompileTarget.WebGPUWgsl || Pass != "point-shadow-depth" ||
             VertexProfile != "static-position-v1" || OutputProfile != "radial-r16f-v1"))
            throw new ArgumentException("OpaquePointShadowDepthV1 requires the WebGPU point-shadow-depth/static-position-v1/radial-r16f-v1 variant.");
        if (Semantic == EngineMaterialSemanticIdentity.OpaqueSpotShadowDepthV1 &&
            (Target != ShaderCompileTarget.WebGPUWgsl || Pass != "spot-shadow-depth" ||
             VertexProfile != "static-position-v1" || OutputProfile != "projected-r16f-v1"))
            throw new ArgumentException("OpaqueSpotShadowDepthV1 requires the WebGPU spot-shadow-depth/static-position-v1/projected-r16f-v1 variant.");
        string? debugProfile = Semantic.Semantic switch
        {
            EngineMaterialSemantic.DebugPoint => "instanced-debug-point-v1",
            EngineMaterialSemantic.DebugLine => "instanced-debug-line-v1",
            EngineMaterialSemantic.DebugTriangle => "instanced-debug-triangle-v1",
            _ => null,
        };
        if (debugProfile is not null &&
            (Target != ShaderCompileTarget.WebGPUWgsl || Pass != "debug-overlay" ||
             VertexProfile != debugProfile || OutputProfile != "display-rgba-v1"))
            throw new ArgumentException($"{Semantic.Semantic}V1 requires the WebGPU debug-overlay/{debugProfile}/display-rgba-v1 variant.");
        string? uiProfile = Semantic.Semantic switch
        {
            EngineMaterialSemantic.UIQuadBatched => "instanced-ui-quad-v1",
            EngineMaterialSemantic.UIQuadBatchedTexture => "instanced-ui-quad-texture-v1",
            EngineMaterialSemantic.UITextBatchedBitmap => "instanced-ui-bitmap-text-v1",
            _ => null,
        };
        if (uiProfile is not null &&
            (Target != ShaderCompileTarget.WebGPUWgsl || Pass != "screen-ui" ||
             VertexProfile != uiProfile || OutputProfile != "display-rgba-v1"))
            throw new ArgumentException($"{Semantic.Semantic}V1 requires the WebGPU screen-ui/{uiProfile}/display-rgba-v1 variant.");
    }

    private bool IsLitCoverageProfile()
        => Pass switch
        {
            "forward-coverage" => VertexProfile == "static-position-normal-v1" &&
                (OutputProfile is "linear-hdr-v1" or "linear-hdr-directional-shadow-v1" or "linear-hdr-local-shadows-v1"),
            "depth-normal" => VertexProfile == "static-position-normal-v1" && OutputProfile == "normal-rgba16f-v1",
            "depth" => VertexProfile == "static-position-v1" && OutputProfile == "depth-normal-v1",
            "point-shadow-depth" => VertexProfile == "static-position-v1" && OutputProfile == "radial-r16f-v1",
            "spot-shadow-depth" => VertexProfile == "static-position-v1" && OutputProfile == "projected-r16f-v1",
            _ => false,
        };

    private static void ValidateProfile(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 || value[0] is < 'a' or > 'z')
            throw new ArgumentException("A variant selector must be a lowercase, bounded identifier.", parameterName);
        foreach (char character in value)
            if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.'))
                throw new ArgumentException("A variant selector must be a lowercase, bounded identifier.", parameterName);
    }
}
