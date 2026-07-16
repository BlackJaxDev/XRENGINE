namespace XREngine.Rendering;

public readonly record struct ResolvedMeshRenderMaterial(
    XRMaterial Material,
    XRMaterial? ShadowUniformSourceMaterial,
    bool IsShadowVariant,
    bool IsDepthNormalVariant,
    string Reason);
