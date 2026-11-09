using XREngine.Rendering;

namespace XREngine.Runtime.Imaging.Bc7Enc;

/// <summary>Installs the bc7enc_rdo texture block encoder for desktop authoring and cook hosts.</summary>
public static class Bc7EncBackend
{
    /// <summary>
    /// Installs the encoder. When the native library cannot load, the method logs the
    /// reason and installs nothing, so the texture cook stores uncompressed RGBA8.
    /// </summary>
    public static void Register()
    {
        if (Bc7EncTextureBlockEncoder.TryCreate(out Bc7EncTextureBlockEncoder? encoder, out string failure))
        {
            TextureBlockEncoderServices.Install(encoder!);
            return;
        }

        Debug.LogWarning($"[TextureCompression] The block-compression encoder is not available, so cooked textures stay uncompressed. Reason: {failure}");
    }
}
