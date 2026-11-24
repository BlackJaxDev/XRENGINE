namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Stable diagnostic codes used by the source toon material conversion pipeline.
/// </summary>
public static class MaterialConversionDiagnosticCodes
{
    public const string UnknownVersion = "TOON0001";
    public const string AmbiguousLockedSignature = "TOON0002";
    public const string UnclassifiedRuntimeProperty = "TOON0003";
    public const string CatalogIdentityMismatch = "TOON0004";
    public const string IntegrationUnavailable = "TOON0005";
    public const string RuntimeMappingMissing = "TOON0006";
    public const string SourceValueNotPreserved = "TOON0007";
    public const string RenderStateDifference = "TOON0008";
    public const string AnimationBindingUnmapped = "TOON0009";
    public const string IntentionalNativeDifference = "TOON0010";
    public const string AssetReferenceMissing = "TOON0011";
    public const string RequestedUvChannelMissing = "TOON0013";
    public const string EnumValueOutOfRange = "TOON0014";
    public const string UnsupportedTextureAsset = "TOON0012";
    public const string PassPrewarmFailed = "TOON0015";
    public const string UnverifiedRelease = "TOON0016";
    public const string ProLossyDowngrade = "TOONLOSS0001";
    public const string ProFeatureDiscarded = "TOONLOSS0002";
}
