namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Lists the release lines of the source toon shader that the converter admits without an exact verified identity.
/// </summary>
/// <remarks>
/// An exact verified identity always has priority. A release line admits the other releases of the same major and
/// minor version and converts them with the rules of the nearest verified version. Each release of a line keeps the
/// property names of the line, so the difference from the behavior version is small. The matcher reports each
/// admission with a warning, because the converter has not checked the exact release.
/// </remarks>
public static class SourceToonReleaseLineCatalog
{
    /// <summary>The admitted release lines, oldest first.</summary>
    public static IReadOnlyList<SourceToonReleaseLine> Lines { get; } =
    [
        new(8, 0, "231d95ff7213dd74f82af136b256fb32", new(8, 0, 426), SourceToon81Catalog.Version),
        new(8, 1, SourceToon81Catalog.OriginalShaderGuid, new(8, 1, 167), SourceToon81Catalog.Version),
        new(9, 0, "23f6705aff8bf964c87bfb3dd66ab345", new(9, 0, 62), SourceToon92Catalog.Version79),
        new(9, 1, "cef5dc16b30fdbb46b08ab424d41d1c0", new(9, 1, 30), SourceToon92Catalog.Version79),
        new(9, 2, SourceToon92Catalog.ShaderGuid, new(9, 2, 80), SourceToon92Catalog.Version79),
        new(9, 3, SourceToon93Catalog.ShaderGuid, new(9, 3, 64), SourceToon93Catalog.Version),
        new(10, 0, SourceToon10Catalog.OriginalShaderGuid, new(10, 0, 29), SourceToon10Catalog.Version),
    ];

    /// <summary>Returns the line that contains the version, or null.</summary>
    public static SourceToonReleaseLine? FindByVersion(SourceToonShaderVersion? version)
    {
        if (version is not SourceToonShaderVersion value)
            return null;

        foreach (SourceToonReleaseLine line in Lines)
        {
            if (line.Contains(value))
                return line;
        }

        return null;
    }

    /// <summary>Returns the line whose main toon shader has the GUID, or null.</summary>
    public static SourceToonReleaseLine? FindByGuid(string? shaderGuid)
    {
        if (string.IsNullOrWhiteSpace(shaderGuid))
            return null;

        foreach (SourceToonReleaseLine line in Lines)
        {
            if (string.Equals(line.ShaderGuid, shaderGuid, StringComparison.OrdinalIgnoreCase))
                return line;
        }

        return null;
    }

    /// <summary>
    /// Returns the verified version whose conversion rules apply to the version. An exact verified version and a
    /// version outside all lines return the version.
    /// </summary>
    public static SourceToonShaderVersion? GetBehaviorVersion(SourceToonShaderVersion? version)
    {
        if (version is null || IsVerified(version.Value))
            return version;

        return FindByVersion(version)?.BehaviorVersion ?? version;
    }

    /// <summary>Returns the admitted lines as text, for diagnostics.</summary>
    public static string DescribeLines()
        => string.Join(", ", Lines);

    private static bool IsVerified(SourceToonShaderVersion version)
        => version == SourceToon81Catalog.Version ||
           SourceToon92Catalog.Supports(version) ||
           version == SourceToon93Catalog.Version ||
           version == SourceToon10Catalog.Version;
}
