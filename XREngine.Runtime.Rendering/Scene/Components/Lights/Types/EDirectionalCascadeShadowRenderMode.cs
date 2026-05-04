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
    /// Renders active cascades through one layered pass by expanding cascade layers through draw instancing.
    /// </summary>
    InstancedLayered = 1,

    /// <summary>
    /// Renders all cascade layers through one layered framebuffer using a geometry shader.
    /// </summary>
    GeometryShader = 2,

    /// <summary>
    /// Selects the fastest supported layered path at runtime, falling back to sequential rendering.
    /// </summary>
    Auto = 3,
}
