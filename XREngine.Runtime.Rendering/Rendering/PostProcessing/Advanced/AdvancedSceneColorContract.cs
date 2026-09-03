namespace XREngine.Rendering;

/// <summary>
/// Rules and resource conventions for scene color snapshots in the Advanced Render Pipeline.
/// </summary>
public static class AdvancedSceneColorContract
{
    public const string SceneColorSnapshotResourceName = "AdvancedShading.SceneColorSnapshot";

    /// <summary>
    /// Determines whether a dedicated scene color copy is required based on active refractive draws.
    /// </summary>
    public static bool RequiresSnapshot(uint refractiveDrawCount, bool hasFeedbackPass)
        => refractiveDrawCount > 0u || hasFeedbackPass;
}
