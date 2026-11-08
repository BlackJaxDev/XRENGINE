using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Maps a texture role to the cache variant folder of the streaming cache, and back.
/// </summary>
/// <remarks>
/// With <see cref="ETextureCookCompression.Bcn"/>, one source file can cook to a different
/// block format for each role (for example BC5 as a normal map and BC7 as color). The role
/// is therefore a variant folder below the payload key: <c>usage-color</c>,
/// <c>usage-data</c>, or <c>usage-normal</c>. The cache codec reads the folder back from the
/// cache path when it cooks the texture. Other compression modes use no role folder.
/// </remarks>
internal static class TextureCookUsageVariant
{
    private const string Prefix = "usage-";
    private const string ColorFolder = Prefix + "color";
    private const string DataFolder = Prefix + "data";
    private const string NormalFolder = Prefix + "normal";

    /// <summary>
    /// Returns the variant folder for a role while the effective cook compression uses
    /// role-dependent formats; otherwise null.
    /// </summary>
    internal static string? ResolveVariantKey(ETextureImportUsage usage)
        => TextureCookBlockCompressor.EffectiveMode == ETextureCookCompression.Bcn
            ? usage switch
            {
                ETextureImportUsage.Normal => NormalFolder,
                ETextureImportUsage.Data => DataFolder,
                _ => ColorFolder,
            }
            : null;

    /// <summary>
    /// Reads the role from a cache path. Returns false when the path has no role folder,
    /// which means the cook does not know the role.
    /// </summary>
    internal static bool TryParse(string? cachePath, out ETextureImportUsage usage)
    {
        usage = ETextureImportUsage.Color;
        if (string.IsNullOrEmpty(cachePath))
            return false;

        ReadOnlySpan<char> remaining = cachePath.AsSpan();
        while (!remaining.IsEmpty)
        {
            int separator = remaining.IndexOfAny('\\', '/');
            ReadOnlySpan<char> segment = separator < 0 ? remaining : remaining[..separator];
            if (segment.Equals(NormalFolder, StringComparison.OrdinalIgnoreCase))
            {
                usage = ETextureImportUsage.Normal;
                return true;
            }
            if (segment.Equals(DataFolder, StringComparison.OrdinalIgnoreCase))
            {
                usage = ETextureImportUsage.Data;
                return true;
            }
            if (segment.Equals(ColorFolder, StringComparison.OrdinalIgnoreCase))
            {
                usage = ETextureImportUsage.Color;
                return true;
            }

            remaining = separator < 0 ? ReadOnlySpan<char>.Empty : remaining[(separator + 1)..];
        }

        return false;
    }
}
