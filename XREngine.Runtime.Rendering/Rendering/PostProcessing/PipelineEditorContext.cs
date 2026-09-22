using XREngine.Components;

namespace XREngine.Rendering.PostProcessing;

/// <summary>
/// Camera and selected pipeline state used by pipeline-owned editor controls.
/// Camera settings remain local to the camera; pipeline settings affect all instances using the selected asset.
/// </summary>
public readonly record struct PipelineEditorContext(
    XRCamera Camera,
    CameraComponent? Component,
    RenderPipeline? ActiveViewportPipeline,
    RenderPipeline SelectedPipeline,
    PipelinePostProcessState? State,
    RenderPipelinePostProcessSchema Schema)
{
    /// <summary>
    /// True when the selected target is used by this camera, allowing camera-wide edits.
    /// Selecting another pipeline for inspection never rebinds the camera.
    /// </summary>
    public bool IsActivePipeline
    {
        get
        {
            if (ReferenceEquals(SelectedPipeline, ActiveViewportPipeline))
                return true;
            foreach (var viewport in Camera.Viewports)
                if (ReferenceEquals(viewport.RenderPipeline, SelectedPipeline))
                    return true;
            return false;
        }
    }
}
