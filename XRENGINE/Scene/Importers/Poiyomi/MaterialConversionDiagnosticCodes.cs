namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// Stable diagnostic codes used by the Poiyomi material conversion pipeline.
/// </summary>
public static class MaterialConversionDiagnosticCodes
{
    public const string UnknownVersion = "POI0001";
    public const string AmbiguousLockedSignature = "POI0002";
    public const string UnclassifiedRuntimeProperty = "POI0003";
    public const string CatalogIdentityMismatch = "POI0004";
    public const string IntegrationUnavailable = "POI0005";
    public const string RuntimeMappingMissing = "POI0006";
    public const string SourceValueNotPreserved = "POI0007";
    public const string RenderStateDifference = "POI0008";
    public const string AnimationBindingUnmapped = "POI0009";
    public const string IntentionalNativeDifference = "POI0010";
    public const string AssetReferenceMissing = "POI0011";
    public const string RequestedUvChannelMissing = "POI0013";
    public const string EnumValueOutOfRange = "POI0014";
    public const string UnsupportedTextureAsset = "POI0012";
}
