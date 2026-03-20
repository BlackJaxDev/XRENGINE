namespace XREngine
{
    /// <summary>
    /// Historical snapshot YAML hook retained only as an explicit published-runtime guard.
    /// Editor/dev snapshot serialization should go through the normal YAML asset pipeline.
    /// </summary>
    internal static class SnapshotYamlSerializer
    {
        public static void EnsureSupported()
            => AssetManager.EnsureYamlAssetRuntimeSupported("play-mode snapshot YAML serialization");
    }
}