namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>Exact shared local function and hash-owned compute and raster companions of a generated material.</summary>
public sealed record ShaderNativeVertexCompanion(string Profile, string FunctionSource, string FunctionSha256, string? ComputeIdentity,
    string? DepthNormalIdentity = null, string? DirectionalShadowIdentity = null, string? PointShadowIdentity = null,
    string? SpotShadowIdentity = null, string? DirectionalReceiverIdentity = null, string? LocalReceiverIdentity = null);
