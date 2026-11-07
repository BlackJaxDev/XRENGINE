using XREngine.Rendering;

namespace XREngine.Cutscenes;

/// <summary>
/// Starts the shader program work of the T0 draws of a cutscene and reports when each one can draw on the active backend.
/// <see cref="CutsceneOpenSequence"/> uses it for the T0 (proxy) draws at the open, and later for the other LOD materials as
/// lower-priority work. All members run on the update thread. They must not wait for the render thread or for a compile.
/// </summary>
public interface ICutsceneProgramPrewarmer
{
    /// <summary>
    /// The render pipeline instance of the view that shows the cutscene camera, or null for no view filter. The open sets it
    /// at each frame. A backend whose pipelines depend on the pass measures the readiness against the passes of this view.
    /// </summary>
    XRRenderPipelineInstance? View { get; set; }

    /// <summary>Starts the program work of a material, and of its draw on a renderer when one is given. It must not block.</summary>
    /// <param name="material">The material.</param>
    /// <param name="renderer">The renderer that draws the material, or null to start only the material work.</param>
    void Request(XRMaterial material, XRMeshRenderer? renderer);

    /// <summary>
    /// Gets the state of the draw of a material on a renderer. It must not block and must not allocate when nothing changed.
    /// A draw that was not requested yet is requested.
    /// </summary>
    /// <param name="material">The material of the draw.</param>
    /// <param name="renderer">The renderer of the draw, or null when no renderer draws the material yet.</param>
    /// <param name="reason">The reason of the state, or null.</param>
    ECutsceneProgramState GetState(XRMaterial material, XRMeshRenderer? renderer, out string? reason);

    /// <summary>
    /// Drops every draw of the open. The open calls it before it collects the draws again, for example after a preview
    /// recompile made new instances.
    /// </summary>
    void Reset();

    /// <summary>Stops the render-thread work of the open. The open calls it when it finished, failed, or was canceled.</summary>
    void Stop();
}
