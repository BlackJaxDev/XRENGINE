using XREngine.Rendering;

namespace XREngine.Scene.Components.Landscape.Interfaces;

/// <summary>
/// Base interface for terrain generation/modification modules.
/// </summary>
public interface ITerrainModule
{
    /// <summary>
    /// Unique name for this module.
    /// </summary>
    string ModuleName { get; }

    /// <summary>
    /// Priority for execution order (lower = earlier).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Whether this module is currently enabled.
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>
    /// Gets the GLSL uniform declarations required by this module.
    /// </summary>
    string GetUniformDeclarations();

    /// <summary>
    /// Sets the uniform values on the shader program.
    /// </summary>
    /// <param name="program">The shader program to set uniforms on.</param>
    void SetUniforms(XRRenderProgram program);
}
