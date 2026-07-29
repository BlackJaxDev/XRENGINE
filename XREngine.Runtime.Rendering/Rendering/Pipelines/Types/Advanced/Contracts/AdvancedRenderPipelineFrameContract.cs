using System.Collections.ObjectModel;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering;

/// <summary>
/// Canonical ordered frame-stage contract shared by OpenGL and Vulkan.
/// Stage implementations may evolve without changing these identities or their ordering.
/// </summary>
public static class AdvancedRenderPipelineFrameContract
{
    private static readonly AdvancedRenderStageDescriptor[] StageDefinitions =
    [
        new(
            EAdvancedRenderStage.FrameBegin,
            "Advanced.FrameBegin",
            "Advanced / Frame Begin",
            ERenderGraphPassStage.Transfer),
        new(
            EAdvancedRenderStage.Deformation,
            "Advanced.Deformation",
            "Advanced / Deformation",
            ERenderGraphPassStage.Compute),
        new(
            EAdvancedRenderStage.VisibilityPreparation,
            "Advanced.VisibilityPreparation",
            "Advanced / Visibility Preparation",
            ERenderGraphPassStage.Compute),
        new(
            EAdvancedRenderStage.VisibilityRaster,
            "Advanced.VisibilityRaster",
            "Advanced / Visibility Raster",
            ERenderGraphPassStage.Graphics),
        new(
            EAdvancedRenderStage.DepthPyramidAndLateVisibility,
            "Advanced.DepthPyramidAndLateVisibility",
            "Advanced / Depth Pyramid And Late Visibility",
            ERenderGraphPassStage.Compute),
        new(
            EAdvancedRenderStage.WorkClassification,
            "Advanced.WorkClassification",
            "Advanced / Work Classification",
            ERenderGraphPassStage.Compute),
        new(
            EAdvancedRenderStage.AttributeReconstruction,
            "Advanced.AttributeReconstruction",
            "Advanced / Attribute Reconstruction",
            ERenderGraphPassStage.Compute),
        new(
            EAdvancedRenderStage.NativeOpaqueShading,
            "Advanced.NativeOpaqueShading",
            "Advanced / Native Opaque Shading",
            ERenderGraphPassStage.Compute),
        new(
            EAdvancedRenderStage.LatePasses,
            "Advanced.LatePasses",
            "Advanced / Late Passes",
            ERenderGraphPassStage.Graphics),
        new(
            EAdvancedRenderStage.TemporalAndPostProcessing,
            "Advanced.TemporalAndPostProcessing",
            "Advanced / Temporal And Post Processing",
            ERenderGraphPassStage.Graphics),
        new(
            EAdvancedRenderStage.Output,
            "Advanced.Output",
            "Advanced / Output",
            ERenderGraphPassStage.Graphics),
        new(
            EAdvancedRenderStage.UserInterface,
            "Advanced.UserInterface",
            "Advanced / User Interface",
            ERenderGraphPassStage.Graphics),
    ];

    private static readonly ReadOnlyCollection<AdvancedRenderStageDescriptor> OrderedStageDefinitions =
        Array.AsReadOnly(StageDefinitions);

    /// <summary>
    /// Ordered stage definitions used to construct and validate the command graph.
    /// </summary>
    public static IReadOnlyList<AdvancedRenderStageDescriptor> OrderedStages
        => OrderedStageDefinitions;

    /// <summary>
    /// Resolves a stage descriptor without allocating.
    /// </summary>
    public static AdvancedRenderStageDescriptor GetDescriptor(EAdvancedRenderStage stage)
    {
        int index = (int)stage;
        if ((uint)index >= (uint)StageDefinitions.Length ||
            StageDefinitions[index].Stage != stage)
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown advanced render stage.");
        }

        return StageDefinitions[index];
    }
}
