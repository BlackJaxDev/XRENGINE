namespace XREngine.Data;

public enum EFontAtlasImportMode
{
    Auto,
    Bitmap,
    Msdf,
    Mtsdf,
}

public sealed class XRFontImportOptions : IXR3rdPartyImportOptions
{
    public EFontAtlasImportMode AtlasMode { get; set; } = EFontAtlasImportMode.Auto;
    public float BitmapFontDrawSize { get; set; } = 192.0f;
    public float MsdfFontSize { get; set; } = 64.0f;
    public float MsdfPixelRange { get; set; } = 8.0f;
    public float MsdfInnerPixelPadding { get; set; } = 1.0f;
    public float MsdfOuterPixelPadding { get; set; } = 2.0f;
    public int MsdfThreadCount { get; set; } = 0;
    public bool AllowBitmapFallback { get; set; } = true;
}
