namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// Pinned source identity and deterministic naming policy for Poiyomi Toon 9.3.64.
/// </summary>
public static class PoiyomiToon93Catalog
{
    public const string VersionText = "9.3.64";
    public const string RepositoryCommit = "c5aaeeb3a67782b7e8a26e184d5e0a1970792294";
    public const string ShaderGuid = "9444ce77bf4418748b1e8591b9d97f85";
    public const string ShaderBlob = "4e3a68b3551e63e6b6c57625669d19e86f70ac8c";
    public const string ShaderSha256 = "7efb9176022291a041ecf332bf999f68ba33591d6f446e60757be83e968e61d8";
    public const string ThryEditorTree = "6437aeaec7b715e7fd000bfd0bdd3d6b0840c6db";
    public const string CatalogResourceName = "XREngine.Scene.Importers.Poiyomi.Catalogs.poiyomi-toon-9.3.64.json";

    public static readonly PoiyomiShaderVersion Version = new(9, 3, 64);

    /// <summary>
    /// Opens the generated machine-readable property, pass, annotation, and workflow catalog.
    /// </summary>
    public static Stream OpenCatalog()
        => typeof(PoiyomiToon93Catalog).Assembly.GetManifestResourceStream(CatalogResourceName)
           ?? throw new InvalidOperationException($"Embedded Poiyomi catalog '{CatalogResourceName}' is missing.");

    public static string GetMaterialName(string sourceAssetPath, string? sourceGuid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAssetPath);
        string stem = SanitizeSegment(Path.GetFileNameWithoutExtension(sourceAssetPath));
        string collisionSuffix = GetGuidSuffix(sourceGuid);
        return $"{stem}.poiyomi-9_3_64{collisionSuffix}.uber";
    }

    public static string GetPassVariantName(string materialName, string passRole, ulong variantHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(materialName);
        ArgumentException.ThrowIfNullOrWhiteSpace(passRole);
        return $"{SanitizeSegment(materialName)}.{SanitizeSegment(passRole)}.{variantHash:x16}";
    }

    public static string GetPreservedMetadataName(string materialName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(materialName);
        return $"{SanitizeSegment(materialName)}.poiyomi-source.json";
    }

    public static string GetAnimationBindingName(string materialName, string semanticProperty)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(materialName);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticProperty);
        return $"{SanitizeSegment(materialName)}/{SanitizeSegment(semanticProperty)}";
    }

    private static string GetGuidSuffix(string? sourceGuid)
    {
        if (string.IsNullOrWhiteSpace(sourceGuid))
            return string.Empty;

        ReadOnlySpan<char> guid = sourceGuid.AsSpan().Trim();
        if (guid.Length < 8)
            throw new ArgumentException("A source GUID must contain at least eight characters.", nameof(sourceGuid));

        for (int index = 0; index < 8; index++)
            if (!Uri.IsHexDigit(guid[index]))
                throw new ArgumentException("A source GUID must begin with hexadecimal characters.", nameof(sourceGuid));

        return $".{guid[..8].ToString().ToLowerInvariant()}";
    }

    private static string SanitizeSegment(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        int length = 0;
        foreach (char character in value)
        {
            char normalized = char.IsLetterOrDigit(character) || character is '_' or '-' or '.'
                ? character
                : '-';
            if (normalized == '-' && length > 0 && buffer[length - 1] == '-')
                continue;

            buffer[length++] = normalized;
        }

        string result = buffer[..length].ToString().Trim('-', '.');
        return result.Length > 0 ? result : "unnamed";
    }
}
