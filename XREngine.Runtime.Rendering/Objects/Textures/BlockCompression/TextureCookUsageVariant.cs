using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Maps a texture role to the cache variant folder of the streaming cache, and back.
/// </summary>
/// <remarks>
/// Data and normal pixels use separate versioned folders so old mips that lost RGB under
/// zero alpha cannot be reused. BCn also uses a color folder because its block format
/// depends on the texture role. The cache codec reads the role from the folder name.
/// </remarks>
internal static class TextureCookUsageVariant
{
    private const string Prefix = "usage-";
    private const string ColorFolder = Prefix + "color";
    private const string DataFolder = Prefix + "data";
    private const string NormalFolder = Prefix + "normal";
    private const string IndependentDataFolder = DataFolder + "-independent-v1";
    private const string IndependentNormalFolder = NormalFolder + "-independent-v1";

    /// <summary>
    /// Returns a new cache folder for data and normal pixels that retain RGB under zero alpha.
    /// Color uses a role folder only when its block format depends on the role.
    /// </summary>
    internal static string? ResolveVariantKey(ETextureImportUsage usage)
        => usage switch
        {
            ETextureImportUsage.Normal => IndependentNormalFolder,
            ETextureImportUsage.Data => IndependentDataFolder,
            _ => TextureCookBlockCompressor.EffectiveMode == ETextureCookCompression.Bcn ? ColorFolder : null,
        };

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
            if (segment.Equals(IndependentNormalFolder, StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(NormalFolder, StringComparison.OrdinalIgnoreCase))
            {
                usage = ETextureImportUsage.Normal;
                return true;
            }
            if (segment.Equals(IndependentDataFolder, StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(DataFolder, StringComparison.OrdinalIgnoreCase))
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
