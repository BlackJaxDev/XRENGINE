using System.Globalization;

namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// A semantic Poiyomi shader version.
/// </summary>
public readonly record struct PoiyomiShaderVersion(int Major, int Minor, int Patch)
{
    public static bool TryParse(string? value, out PoiyomiShaderVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        ReadOnlySpan<string> parts = value.Split('.');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
        {
            return false;
        }

        version = new PoiyomiShaderVersion(major, minor, patch);
        return true;
    }

    /// <inheritdoc />
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
