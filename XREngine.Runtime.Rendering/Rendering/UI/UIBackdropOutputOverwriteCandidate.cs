using XREngine.Data.Geometry;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI;

/// <summary>Describes the engine-owned mono window presentation draw.</summary>
public readonly struct UIBackdropOutputOverwriteCandidate
{
    internal UIBackdropOutputOverwriteCandidate(
        AbstractRenderer renderer, XRWindow window, long backendGeneration,
        ulong dispatchToken, BoundingRectangle region,
        XRMeshRenderer meshRenderer, XRMesh geometry, XRMaterial material,
        XRShader vertexShader, XRShader fragmentShader,
        object canonicalKey)
    {
        Renderer = renderer;
        Window = window;
        WindowGlContext = window.DesktopGlContext;
        WindowGlContextGeneration = WindowGlContext?.OwnerGeneration ?? 0L;
        BackendGeneration = backendGeneration;
        DispatchToken = dispatchToken;
        Region = region;
        MeshRenderer = meshRenderer;
        Geometry = geometry;
        GeometryRevision = geometry.GeometryRevision;
        Material = material;
        VertexShader = vertexShader;
        VertexShaderRevision = vertexShader.SourceRevision;
        FragmentShader = fragmentShader;
        FragmentShaderRevision = fragmentShader.SourceRevision;
        CanonicalKey = canonicalKey;
    }

    public AbstractRenderer? Renderer { get; }
    public XRWindow? Window { get; }
    public IRuntimeWindowGlContext? WindowGlContext { get; }
    public long WindowGlContextGeneration { get; }
    public long BackendGeneration { get; }
    public ulong DispatchToken { get; }
    public BoundingRectangle Region { get; }
    public XRMeshRenderer? MeshRenderer { get; }
    public XRMesh? Geometry { get; }
    public long GeometryRevision { get; }
    public XRMaterial? Material { get; }
    public XRShader? VertexShader { get; }
    public long VertexShaderRevision { get; }
    public XRShader? FragmentShader { get; }
    public long FragmentShaderRevision { get; }
    internal object? CanonicalKey { get; }
}
