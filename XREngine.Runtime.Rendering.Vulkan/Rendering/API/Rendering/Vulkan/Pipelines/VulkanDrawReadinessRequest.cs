namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One active draw readiness question: a mesh renderer, the material of its draw, and the view whose passes must draw it.
/// The query thread renews <see cref="LastQueryTicks"/>; the frame-loop readiness prewarm writes the result. The
/// <see cref="VulkanDrawReadinessRegistry"/> lock protects the result fields.
/// </summary>
/// <param name="renderer">The mesh renderer.</param>
/// <param name="material">The material of the draw.</param>
/// <param name="view">The render pipeline instance of the view, or null for every view.</param>
/// <param name="stereo">True when only stereo passes count.</param>
internal sealed class VulkanDrawReadinessRequest(VkMeshRenderer renderer, XRMaterial material, XRRenderPipelineInstance? view, bool stereo)
{
    /// <summary>The mesh renderer.</summary>
    public VkMeshRenderer Renderer { get; } = renderer;

    /// <summary>The material of the draw.</summary>
    public XRMaterial Material { get; } = material;

    /// <summary>The render pipeline instance of the view, or null for every view.</summary>
    public XRRenderPipelineInstance? View { get; } = view;

    /// <summary>True when only stereo passes count.</summary>
    public bool Stereo { get; } = stereo;

    /// <summary>The time of the last query, from <see cref="Environment.TickCount64"/>.</summary>
    public long LastQueryTicks;

    /// <summary>The number of frame-loop evaluations since the request started. Zero means no frame checked it yet.</summary>
    public long Evaluations;

    /// <summary>The state of the last evaluation.</summary>
    public ERenderDrawReadiness State = ERenderDrawReadiness.NotStarted;

    /// <summary>The reason of the last evaluation.</summary>
    public string Reason = "no frame checked the passes of the view yet";

    /// <summary>True when the request asks the same question.</summary>
    public bool Matches(VkMeshRenderer renderer, XRMaterial material, XRRenderPipelineInstance? view, bool stereo)
        => ReferenceEquals(Renderer, renderer) &&
           ReferenceEquals(Material, material) &&
           ReferenceEquals(View, view) &&
           Stereo == stereo;
}
