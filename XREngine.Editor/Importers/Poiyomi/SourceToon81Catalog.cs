using System.Security.Cryptography;
using System.Text;

namespace XREngine.Scene.Importers.SourceToon;

/// <summary>Identifies the verified locked Toon shader used by the Aeri Emote material.</summary>
public static class SourceToon81Catalog
{
    public static readonly SourceToonShaderVersion Version = new(8, 1, 167);
    public const string ShaderGuid = "2788d270d440b8a4d932cc16dc223bdf";
    public const string OriginalShaderGuid = "5ca92f1e9fc35504aba297fd1acd62df";
    public const string ShaderSha256 = "00669ff7abfffcfd83f406d0bc54e520f63e84b08cf99b8cd647f35e71957553";

    public static bool Matches(SourceToonShaderMatchInput input, SourceToonShaderVersion? version)
    {
        if (version != Version || input.ShaderSource is null ||
            !string.Equals(input.ShaderGuid, ShaderGuid, StringComparison.OrdinalIgnoreCase) ||
            !input.OverrideTags.TryGetValue("OriginalShaderGUID", out string? originalGuid) ||
            !string.Equals(originalGuid, OriginalShaderGuid, StringComparison.OrdinalIgnoreCase))
            return false;

        byte[] sourceHash = SHA256.HashData(Encoding.UTF8.GetBytes(input.ShaderSource));
        return string.Equals(Convert.ToHexStringLower(sourceHash), ShaderSha256, StringComparison.Ordinal);
    }
}
