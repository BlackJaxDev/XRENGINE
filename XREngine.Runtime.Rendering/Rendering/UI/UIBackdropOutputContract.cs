using XREngine.Data.Geometry;

namespace XREngine.Rendering.UI;

/// <summary>Checks exact output coverage and capture extents.</summary>
public static class UIBackdropOutputContract
{
    private static readonly object CanonicalWindowPresentKey = new();

    internal static UIBackdropOutputOverwriteCandidate CreateCanonicalWindowPresentCandidate(
        AbstractRenderer renderer, XRWindow window, long backendGeneration,
        ulong dispatchToken, BoundingRectangle region,
        XRMeshRenderer meshRenderer, XRMesh geometry, XRMaterial material,
        long canonicalGeometryRevision, long canonicalVertexShaderRevision,
        long canonicalFragmentShaderRevision, string canonicalFragmentSource)
    {
        if (geometry.GeometryRevision != canonicalGeometryRevision ||
            material.VertexShaders.Count != 1 || material.FragmentShaders.Count != 1 ||
            material.GeometryShaders.Count != 0 || material.TessCtrlShaders.Count != 0 ||
            material.TessEvalShaders.Count != 0 ||
            material.VertexShaders[0].SourceRevision != canonicalVertexShaderRevision ||
            material.FragmentShaders[0].SourceRevision != canonicalFragmentShaderRevision ||
            material.FragmentShaders[0].Source.Text != canonicalFragmentSource)
            return default;
        return new(renderer, window, backendGeneration, dispatchToken, region,
            meshRenderer, geometry, material, material.VertexShaders[0],
            material.FragmentShaders[0], CanonicalWindowPresentKey);
    }

    internal static bool IsCanonicalWindowPresentCandidate(
        in UIBackdropOutputOverwriteCandidate candidate,
        XRMeshRenderer meshRenderer, XRMesh? geometry, XRMaterial? material)
        => ReferenceEquals(candidate.CanonicalKey, CanonicalWindowPresentKey) &&
           ReferenceEquals(candidate.MeshRenderer, meshRenderer) &&
           ReferenceEquals(candidate.Geometry, geometry) &&
           candidate.GeometryRevision == geometry?.GeometryRevision &&
           ReferenceEquals(candidate.Material, material) &&
           material is not null && material.VertexShaders.Count == 1 &&
           material.FragmentShaders.Count == 1 &&
           material.GeometryShaders.Count == 0 && material.TessCtrlShaders.Count == 0 &&
           material.TessEvalShaders.Count == 0 &&
           ReferenceEquals(candidate.VertexShader, material.VertexShaders[0]) &&
           ReferenceEquals(candidate.FragmentShader, material.FragmentShaders[0]) &&
           candidate.VertexShaderRevision == material.VertexShaders[0].SourceRevision &&
           candidate.FragmentShaderRevision == material.FragmentShaders[0].SourceRevision;

    public static bool Covers(in BoundingRectangle completed, in BoundingRectangle requested)
        => completed.Width > 0 && completed.Height > 0 &&
           requested.Width > 0 && requested.Height > 0 &&
           completed.X <= requested.X && completed.Y <= requested.Y &&
           (long)completed.X + completed.Width >= (long)requested.X + requested.Width &&
           (long)completed.Y + completed.Height >= (long)requested.Y + requested.Height;

    public static bool AllowsBackbufferCopy(in BoundingRectangle source,
        int backbufferWidth, int backbufferHeight,
        uint destinationWidth, uint destinationHeight)
        => source.X >= 0 && source.Y >= 0 &&
           source.Width > 0 && source.Height > 0 &&
           backbufferWidth > 0 && backbufferHeight > 0 &&
           destinationWidth > 0 && destinationHeight > 0 &&
           (long)source.X + source.Width <= backbufferWidth &&
           (long)source.Y + source.Height <= backbufferHeight;
}
