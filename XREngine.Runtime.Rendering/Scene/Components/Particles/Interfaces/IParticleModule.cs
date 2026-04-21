using XREngine.Rendering;

namespace XREngine.Scene.Components.Particles.Interfaces;

/// <summary>
/// Base interface for all particle system modules.
/// Modules are modular pieces of functionality that can be composed to create different particle behaviors.
/// </summary>
public interface IParticleModule
{
    /// <summary>
    /// Unique name for the module, used for shader generation.
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
