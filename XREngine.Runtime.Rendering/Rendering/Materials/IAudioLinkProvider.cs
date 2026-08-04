namespace XREngine.Rendering.Materials;

/// <summary>
/// Supplies a stable, provider-owned AudioLink texture and its frame state.
/// The texture uses bands in columns and newest-to-oldest history in rows.
/// Providers update the same resource in place; materials never replace it
/// per frame.
/// </summary>
public interface IAudioLinkProvider
{
    XRTexture2D DataTexture { get; }

    bool TryGetFrame(out AudioLinkFrame frame);
}
