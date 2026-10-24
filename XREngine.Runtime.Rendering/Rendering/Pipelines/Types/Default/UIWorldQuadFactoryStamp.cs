namespace XREngine.Rendering;

/// <summary>Immutable factory authority for one default-pipeline fullscreen quad.</summary>
internal sealed class UIWorldQuadFactoryStamp
{
    private readonly XRQuadFrameBuffer _quad;
    private readonly XRMaterial _material;
    private readonly XRMesh _mesh;
    private readonly XRShader _vertex;
    private readonly XRShader _fragment;
    private readonly long _geometryRevision;
    private readonly long _vertexRevision;
    private readonly long _fragmentRevision;
    private readonly string? _vertexSource;
    private readonly string? _fragmentSource;

    private UIWorldQuadFactoryStamp(XRQuadFrameBuffer quad)
    {
        _quad = quad;
        _material = quad.Material ??
            throw new InvalidOperationException("World UI quad has no factory material.");
        _mesh = quad.FullScreenMesh.Mesh ??
            throw new InvalidOperationException("World UI quad has no factory mesh.");
        if (!SharedRenderHelperGeometry.IsCurrentFullscreenTriangle(_mesh) ||
            _material.VertexShaders.Count != 1 ||
            _material.FragmentShaders.Count != 1 ||
            _material.Shaders.Count != 2 ||
            _material.GeometryShaders.Count != 0 ||
            _material.TessCtrlShaders.Count != 0 ||
            _material.TessEvalShaders.Count != 0)
            throw new InvalidOperationException("World UI quad has no canonical factory program.");
        _vertex = _material.VertexShaders[0];
        _fragment = _material.FragmentShaders[0];
        _geometryRevision = _mesh.GeometryRevision;
        _vertexRevision = _vertex.SourceRevision;
        _fragmentRevision = _fragment.SourceRevision;
        _vertexSource = _vertex.Source?.Text;
        _fragmentSource = _fragment.Source?.Text;
    }

    internal static bool TryCreate(XRQuadFrameBuffer quad, out UIWorldQuadFactoryStamp? stamp)
    {
        stamp = null;
        XRMaterial? material = quad.Material;
        XRMesh? mesh = quad.FullScreenMesh.Mesh;
        if (material is null || mesh is null ||
            !SharedRenderHelperGeometry.IsCurrentFullscreenTriangle(mesh) ||
            material.VertexShaders.Count != 1 || material.FragmentShaders.Count != 1 ||
            material.Shaders.Count != 2 || material.GeometryShaders.Count != 0 ||
            material.TessCtrlShaders.Count != 0 || material.TessEvalShaders.Count != 0)
            return false;

        stamp = new UIWorldQuadFactoryStamp(quad);
        return true;
    }

    internal bool IsCurrent(XRQuadFrameBuffer quad)
        => ReferenceEquals(quad, _quad) &&
            ReferenceEquals(quad.Material, _material) &&
            ReferenceEquals(quad.FullScreenMesh.Material, _material) &&
            ReferenceEquals(quad.FullScreenMesh.Mesh, _mesh) &&
            _mesh.GeometryRevision == _geometryRevision &&
            SharedRenderHelperGeometry.IsCurrentFullscreenTriangle(_mesh) &&
            _material.Shaders.Count == 2 &&
            _material.VertexShaders.Count == 1 &&
            _material.FragmentShaders.Count == 1 &&
            _material.GeometryShaders.Count == 0 &&
            _material.TessCtrlShaders.Count == 0 &&
            _material.TessEvalShaders.Count == 0 &&
            ReferenceEquals(_material.VertexShaders[0], _vertex) &&
            ReferenceEquals(_material.FragmentShaders[0], _fragment) &&
            _vertex.SourceRevision == _vertexRevision &&
            _fragment.SourceRevision == _fragmentRevision &&
            string.Equals(_vertex.Source?.Text, _vertexSource, StringComparison.Ordinal) &&
            string.Equals(_fragment.Source?.Text, _fragmentSource, StringComparison.Ordinal);

    internal bool IsCurrentPresentMesh(XRMeshRenderer mesh, XRMaterial material)
        => ReferenceEquals(_quad.FullScreenMesh, mesh) &&
            ReferenceEquals(_material, material) && IsCurrent(_quad);
}
