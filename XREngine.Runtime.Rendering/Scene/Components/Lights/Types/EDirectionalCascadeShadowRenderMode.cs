namespace XREngine.Components.Lights;

/// <summary>
/// Selects how a directional light renders its cascaded shadow-map layers.
/// </summary>
public enum EDirectionalCascadeShadowRenderMode
{
    /// <summary>
    /// Renders one cascade viewport and one texture-array layer at a time.
    /// </summary>
    Sequential = 0,

    /// <summary>
    /// Reserved for a future instanced layered path that writes gl_Layer from the vertex stage.
    /// </summary>
    InstancedLayered = 1,

    /// <summary>
    /// Renders all cascade layers through one layered framebuffer using a geometry shader.
    /// </summary>
    GeometryShader = 2,
}
