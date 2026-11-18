namespace XREngine.Rendering.Shadows;

public sealed partial class ShadowAtlasManager
{
    /// <summary>
    /// Atlas planning frame counter, in the same units as <see cref="ShadowAtlasAllocation.LastRenderedFrame"/>.
    /// Diagnostic tools compare the two to show how many atlas frames ago a tile was rendered.
    /// </summary>
    public ulong CurrentFrameIdForDiagnostics => _frameId;
}
