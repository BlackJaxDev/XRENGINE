namespace XREngine.Runtime.Bootstrap;

public class UnitTestingRenderSettings
{
    public ERenderLibrary RenderBackend { get; set; } = ERenderLibrary.OpenGL;
    public RenderBackendFallbackPolicy BackendFallbackPolicy { get; set; } = RenderBackendFallbackPolicy.RequireRequested;

    /// <summary>
    /// Scene pipeline created by bootstrap cameras. Explicit selections take precedence over automatic pipeline policy.
    /// </summary>
    public UnitTestingRenderPipeline RenderPipeline { get; set; } = UnitTestingRenderPipeline.AdvancedRenderPipeline;

    /// <summary>
    /// Path to a <c>.xrs</c> render-pipeline script when <see cref="RenderPipeline"/> is <see cref="UnitTestingRenderPipeline.CustomRenderPipeline"/>.
    /// Relative paths are resolved from the process working directory.
    /// </summary>
    public string? CustomRenderPipelineScriptPath { get; set; }

    /// <summary>
    /// Sets the <c>AdvancedVirtualGeometry</c> engine setting: Vulkan Advanced visibility draws a
    /// cluster cut of models cooked with a cluster hierarchy (<c>GenerateClusterHierarchy</c>).
    /// </summary>
    public bool VirtualGeometry { get; set; }

    /// <summary>
    /// Sets the <c>AdvancedSkinnedVirtualGeometry</c> engine setting: with <see cref="VirtualGeometry"/>
    /// on, skinned meshlet draws also get the cluster cut. True (the default) matches the engine
    /// default; false keeps skinned draws at full detail.
    /// </summary>
    public bool SkinnedVirtualGeometry { get; set; } = true;

    /// <summary>
    /// Sets the <c>AdvancedSkinnedPosedClusterBounds</c> engine setting: with
    /// <see cref="SkinnedVirtualGeometry"/> on, true (the default) bounds each skinned cluster group
    /// by its posed bone box, so distant parts of a skinned draw get coarser clusters and clusters
    /// outside the view are culled; false uses one distance per skinned draw and culls no skinned
    /// cluster.
    /// </summary>
    public bool SkinnedPosedClusterBounds { get; set; } = true;

    /// <summary>
    /// Sets the <c>AdvancedSkinnedDeformationLod</c> engine setting: with <see cref="SkinnedVirtualGeometry"/>
    /// on, far skinned draws deform only the vertices of their coarsest allowed cluster level.
    /// False (the default) deforms every vertex.
    /// </summary>
    public bool SkinnedDeformationLod { get; set; }

    /// <summary>
    /// Sets the <c>AdvancedVirtualGeometryPixelThreshold</c> engine setting: the cluster cut
    /// threshold in render pixels of projected error.
    /// </summary>
    public float VirtualGeometryPixelThreshold { get; set; } = 1.0f;
    public UnitTestingOpenGLRenderSettings OpenGL { get; set; } = new();
    public UnitTestingVulkanRenderSettings Vulkan { get; set; } = new();
}
