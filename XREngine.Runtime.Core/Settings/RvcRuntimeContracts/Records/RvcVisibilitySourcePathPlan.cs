namespace XREngine;

public readonly record struct RvcVisibilitySourcePathPlan(
    ERvcVisibilitySourcePath EnabledPaths,
    ERvcVisibilitySourcePath RequiredPaths,
    bool StaticMeshPathUsesDirectDrawIdentity,
    bool SkinnedPathConsumesComputeOutput,
    bool ZeroReadbackMaterialRowsRequired,
    bool MeshletPathOptional,
    ERvcFallbackReason FallbackReason,
    string Diagnostic)
{
    public bool HasRequiredPaths => (EnabledPaths & RequiredPaths) == RequiredPaths;
    public bool HasGpuCacheSource => (EnabledPaths & ~ERvcVisibilitySourcePath.ForwardPlusOracle) != 0;

    public static RvcVisibilitySourcePathPlan Resolve(
        in RvcPipelineResolution resolution,
        bool staticMeshPathAvailable,
        bool skinnedComputePathAvailable,
        bool zeroReadbackMaterialRowsAvailable,
        bool meshletPathAvailable)
    {
        ERvcVisibilitySourcePath enabled = ERvcVisibilitySourcePath.ForwardPlusOracle;
        if (staticMeshPathAvailable)
            enabled |= ERvcVisibilitySourcePath.StaticMeshDirect;
        if (skinnedComputePathAvailable)
            enabled |= ERvcVisibilitySourcePath.SkinnedComputeOutput;
        if (zeroReadbackMaterialRowsAvailable)
            enabled |= ERvcVisibilitySourcePath.ZeroReadbackMaterialTable;
        if (meshletPathAvailable)
            enabled |= ERvcVisibilitySourcePath.MeshletTaskExpansion;

        ERvcVisibilitySourcePath required = resolution.IsRvcActive
            ? ERvcVisibilitySourcePath.StaticMeshDirect | ERvcVisibilitySourcePath.ZeroReadbackMaterialTable
            : ERvcVisibilitySourcePath.ForwardPlusOracle;

        bool hasRequired = (enabled & required) == required;
        return new(
            enabled,
            required,
            StaticMeshPathUsesDirectDrawIdentity: staticMeshPathAvailable,
            SkinnedPathConsumesComputeOutput: skinnedComputePathAvailable,
            ZeroReadbackMaterialRowsRequired: resolution.IsRvcActive,
            MeshletPathOptional: true,
            hasRequired ? ERvcFallbackReason.None : ERvcFallbackReason.MissingVisibilitySourcePath,
            hasRequired
                ? "RVC visibility source paths are available for the resolved mode."
                : "RVC requires static mesh identity and zero-readback material rows before cache passes can run.");
    }
}
