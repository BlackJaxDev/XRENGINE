namespace XREngine.Rendering;

/// <summary>Inputs for an optional native distance-field font atlas authoring backend.</summary>
public readonly record struct FontDistanceFieldAtlasRequest(
    string FontPath,
    string AtlasPath,
    string MetadataPath,
    EFontAtlasType AtlasType,
    IReadOnlyCollection<uint> CharacterSet,
    float FontSize,
    float PixelRange,
    float InnerPixelPadding,
    float OuterPixelPadding,
    int ThreadCount);
