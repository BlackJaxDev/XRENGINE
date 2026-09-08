namespace XREngine.Rendering;

/// <summary>
/// Rules and resource conventions for scene color snapshots in the Advanced Render Pipeline.
/// </summary>
public static class AdvancedSceneColorContract
{
    public const string SceneColorSnapshotResourceName = "AdvancedShading.SceneColorSnapshot";
    public const string SceneColorSamplerName = "AdvancedSceneColorSnapshot";

    /// <summary>
    /// Determines whether a dedicated scene color copy is required based on active refractive draws.
    /// </summary>
    public static bool RequiresSnapshot(uint refractiveDrawCount, bool hasFeedbackPass)
        => refractiveDrawCount > 0u || hasFeedbackPass;

    /// <summary>
    /// Binds the current Advanced-pipeline snapshot only for a material that
    /// explicitly authored a scene-color dependency. The copy predicate reads
    /// the same published pass metadata, so a bound consumer never relies on a
    /// shader-name or index-of-refraction heuristic.
    /// </summary>
    internal static void ApplyMaterialBinding(
        XRMaterial material,
        XRRenderProgram program)
    {
        AdvancedLatePassMetadata? metadata = material.AdvancedLatePassMetadata;
        if (metadata?.RequiresSceneColorSnapshot != true ||
            string.IsNullOrWhiteSpace(metadata.SceneColorSamplerName) ||
            metadata.SceneColorTextureUnit < 0)
        {
            return;
        }

        XRRenderPipelineInstance? instance =
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (instance?.Pipeline is not AdvancedRenderPipeline ||
            instance.GetTexture<XRTexture>(SceneColorSnapshotResourceName) is not
                XRTexture snapshot)
        {
            return;
        }

        program.Sampler(
            metadata.SceneColorSamplerName,
            snapshot,
            metadata.SceneColorTextureUnit);
    }
}
