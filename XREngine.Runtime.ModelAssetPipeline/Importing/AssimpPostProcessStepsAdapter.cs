using Assimp;

namespace XREngine.Rendering.Models;

/// <summary>Confines the Assimp preprocessing enum to the ModelAssetPipeline implementation.</summary>
internal static class AssimpPostProcessStepsAdapter
{
    public static PostProcessSteps ToAssimp(this ModelImportSteps steps)
        => (PostProcessSteps)(uint)steps;

    public static ModelImportSteps ToModelImportSteps(this PostProcessSteps steps)
        => (ModelImportSteps)(uint)steps;
}
