using XREngine.Rendering;

namespace XREngine.Cutscenes;

/// <summary>
/// The default program prewarmer. It uses the material program path of the engine: the asynchronous uber variant build
/// (<see cref="XRMaterial.RequestUberVariantPreparationIfNeeded"/>) and its readiness state
/// (<see cref="XRMaterial.UberVariantStatus"/> and <see cref="XRMaterial.IsUberVariantReadyForRendering"/>).
/// </summary>
/// <remarks>
/// A material without uber state needs no variant build, so it is ready at once. This layer cannot see the backend link of
/// the program or a Vulkan pipeline compile, because the mesh renderer of the backend makes those objects at the first draw.
/// A failed variant build gives <see cref="ECutsceneProgramState.Failed"/> with the reason of the build.
/// </remarks>
public sealed class CutsceneMaterialProgramPrewarmer : ICutsceneProgramPrewarmer
{
    /// <summary>The shared instance. The prewarmer has no state.</summary>
    public static CutsceneMaterialProgramPrewarmer Instance { get; } = new();

    /// <inheritdoc/>
    public void Request(XRMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        material.RequestUberVariantPreparationIfNeeded();
    }

    /// <inheritdoc/>
    public ECutsceneProgramState GetState(XRMaterial material, out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(material);
        UberMaterialVariantStatus status = material.UberVariantStatus;
        if (status.Stage == EUberMaterialVariantStage.Failed)
        {
            failureReason = status.FailureReason ?? "the uber shader variant build failed";
            return ECutsceneProgramState.Failed;
        }

        failureReason = null;
        return material.IsUberVariantReadyForRendering() ? ECutsceneProgramState.Ready : ECutsceneProgramState.Pending;
    }
}
