namespace XREngine;

internal sealed class RuntimeDebugPreferences
{
    public bool EnableGpuRenderPipelineProfiling { get; set; }
    public bool EnableExactTransparencyTechniques { get; set; }
    public int DepthPeelingMaxLayers { get; set; } = 4;
    public int DepthPeelingPreviewLayer { get; set; }
    public bool VisualizeTransformId { get; set; }
    public bool VisualizeTransparencyAccumulation { get; set; }
    public bool VisualizeTransparencyRevealage { get; set; }
    public bool VisualizeTransparencyOverdrawHeatmap { get; set; }
    public bool VisualizePerPixelLinkedListFragments { get; set; }
    public bool VisualizeDepthPeelingLayer { get; set; }
    public bool RenderLightProbeTetrahedra { get; set; }
    public bool VisualizeDirectionalLightVolumes { get; set; }
    public bool RenderMesh3DBounds { get; set; }
    public bool Preview3DWorldOctree { get; set; }
    public bool Preview2DWorldQuadtree { get; set; }
    public bool AllowGpuCpuFallback { get; set; } = true;
    public bool VisualizeTransparencyModeOverlay { get; set; }
    public bool VisualizeTransparencyClassificationOverlay { get; set; }
    public bool EnableZeroReadbackMaterialScatter { get; set; }
    public EZeroReadbackMaterialDrawPath ZeroReadbackMaterialDrawPath { get; set; } = EZeroReadbackMaterialDrawPath.FullBucketScan;
    public bool ForceGpuPassthroughCulling { get; set; }
}
