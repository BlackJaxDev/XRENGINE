namespace XREngine.Components.Capture.Lights.Types;

/// <summary>
/// Selects the legacy point-light cubemap shadow render strategy.
/// </summary>
public enum EPointShadowRenderMode
{
    /// <summary>
    /// Render each cubemap face in its own pass.
    /// </summary>
    Sequential = 0,

    /// <summary>
    /// Render all cubemap faces in one pass by deriving the face from <c>gl_InstanceID</c>
    /// and writing <c>gl_Layer</c> in the vertex stage.
    /// </summary>
    InstancedLayered = 1,

    /// <summary>
    /// Render all cubemap faces in one pass by fanning triangles out from a geometry shader.
    /// </summary>
    GeometryShader = 2,

}
