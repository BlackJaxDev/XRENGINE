namespace XREngine.Rendering;

/// <summary>
/// Stores the texture block encoder that the application or cook host installs. When no
/// encoder is installed, the texture cook stores uncompressed RGBA8 and logs why.
/// </summary>
public static class TextureBlockEncoderServices
{
    private static ITextureBlockEncoder? _current;

    /// <summary>The installed encoder, or null.</summary>
    public static ITextureBlockEncoder? Current => Volatile.Read(ref _current);

    /// <summary>Installs the encoder that the texture cook uses.</summary>
    public static void Install(ITextureBlockEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        Volatile.Write(ref _current, encoder);
    }
}
