using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One active draw readiness question: a mesh renderer, the material and render options of its draw, and the view whose
/// passes must draw it. The query thread renews <see cref="LastQueryTicks"/>; the frame-loop readiness prewarm writes the
/// result. The <see cref="VulkanDrawReadinessRegistry"/> lock protects the result fields.
/// </summary>
/// <param name="renderer">The mesh renderer.</param>
/// <param name="material">The material of the draw.</param>
/// <param name="renderOptionsOverride">The render options override of the renderable, or null.</param>
/// <param name="view">The render pipeline instance of the view, or null for every view.</param>
/// <param name="stereo">True when only stereo passes count.</param>
internal sealed class VulkanDrawReadinessRequest(
    VkMeshRenderer renderer,
    XRMaterial material,
    RenderingParameters? renderOptionsOverride,
    XRRenderPipelineInstance? view,
    bool stereo)
{
    /// <summary>The mesh renderer.</summary>
    public VkMeshRenderer Renderer { get; } = renderer;

    /// <summary>The material of the draw.</summary>
    public XRMaterial Material { get; } = material;

    /// <summary>The render options override of the renderable, or null. The draw state uses it in place of the material options.</summary>
    public RenderingParameters? RenderOptionsOverride { get; } = renderOptionsOverride;

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

    /// <summary>The program of the renderer when the last evaluation was published, or null.</summary>
    public object? PublishedProgram;

    /// <summary>The link generation of <see cref="PublishedProgram"/> when the last evaluation was published.</summary>
    public ulong PublishedLinkGeneration;

    /// <summary>The shared graphics pipeline retirement generation when the last evaluation was published.</summary>
    public long PublishedRetirementGeneration;

    /// <summary>The target compatibility signature (formats, sample counts, render passes) when the last evaluation was published.</summary>
    public ulong PublishedTargetSignature;

    /// <summary>True when the request asks the same question.</summary>
    public bool Matches(
        VkMeshRenderer renderer,
        XRMaterial material,
        RenderingParameters? renderOptionsOverride,
        XRRenderPipelineInstance? view,
        bool stereo)
        => ReferenceEquals(Renderer, renderer) &&
           ReferenceEquals(Material, material) &&
           ReferenceEquals(RenderOptionsOverride, renderOptionsOverride) &&
           ReferenceEquals(View, view) &&
           Stereo == stereo;
}
