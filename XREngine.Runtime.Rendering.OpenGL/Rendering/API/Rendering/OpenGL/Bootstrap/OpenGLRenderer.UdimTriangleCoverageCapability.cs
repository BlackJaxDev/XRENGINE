using XREngine.Rendering;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// Declares that the OpenGL backend supports per-triangle UDIM coverage buffers for forward Uber draws.
/// </summary>
public partial class OpenGLRenderer : IAuthoredUdimTriangleCoverageBackendCapability
{
    bool IAuthoredUdimTriangleCoverageBackendCapability.SupportsUdimTriangleCoverage => true;
}
