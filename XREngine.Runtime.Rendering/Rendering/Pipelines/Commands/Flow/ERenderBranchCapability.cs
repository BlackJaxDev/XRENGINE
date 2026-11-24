namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>Render state and backend capabilities that a <see cref="RenderBranchCondition"/> can test.</summary>
public enum ERenderBranchCapability
{
    /// <summary>The current pass renders both eyes in one pass.</summary>
    StereoPass,

    /// <summary>The current pass renders a shadow map.</summary>
    ShadowPass,

    /// <summary>The current pass renders a mirror view.</summary>
    MirrorPass,

    /// <summary>The current pass renders a scene capture.</summary>
    SceneCapturePass,

    /// <summary>The current pass renders a light probe.</summary>
    LightProbePass,

    /// <summary>The current pass is not a mirror, scene capture, or light probe pass.</summary>
    MainPass,

    /// <summary>The OpenGL driver supports OVR multiview.</summary>
    MultiView,

    /// <summary>The active renderer is OpenGL.</summary>
    OpenGL,

    /// <summary>The active renderer is Vulkan.</summary>
    Vulkan,

    /// <summary>The active renderer is WebGPU.</summary>
    WebGpu,
}
