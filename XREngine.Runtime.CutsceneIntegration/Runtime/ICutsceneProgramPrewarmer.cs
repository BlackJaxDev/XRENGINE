using XREngine.Rendering;

namespace XREngine.Cutscenes;

/// <summary>
/// Starts the shader program work of materials and reports when it is ready. <see cref="CutsceneOpenSequence"/> uses it for
/// the T0 (proxy) materials at the open, and later for the other LOD materials as lower-priority work. Both members run on
/// the update thread. They must not wait for the render thread or for a compile.
/// </summary>
public interface ICutsceneProgramPrewarmer
{
    /// <summary>Starts the program work of a material if it is not started or complete. It must not block.</summary>
    /// <param name="material">The material.</param>
    void Request(XRMaterial material);

    /// <summary>Gets the state of the program work of a material. It must not block and must not allocate when nothing changed.</summary>
    /// <param name="material">The material.</param>
    /// <param name="failureReason">The reason of a failure, or null.</param>
    ECutsceneProgramState GetState(XRMaterial material, out string? failureReason);
}
