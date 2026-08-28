namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Output-affecting compatibility versions for the model binary cache and its identity policy.
/// </summary>
public static class ModelBinaryCacheVersions
{
    public const uint Schema = 1;
    public const uint Payload = 1;
    public const uint ContainerCodec = 1;
    public const uint CookPolicy = 1;
    public const uint ImportSettingsProjection = 1;
    public const uint SourceIdentityPolicy = 1;
    public const uint CachePathPolicy = 1;
    public const uint VariantFingerprint = 1;
    public const uint HashingPolicy = 1;
    public const uint DeterministicOrdering = 1;
}
