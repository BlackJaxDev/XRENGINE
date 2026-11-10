using System.Security.Cryptography;
using System.Text;

namespace XREngine.Scene.Importers.SourceToon;

/// <summary>Identifies the verified locked shader used by the Horns material.</summary>
public static class SourceToonZekkSoftOutlineCatalog
{
    public const string ShaderGuid = "0f9d42ed730975f45a95b53b7a88d7b9";
    public const string ShaderSha256 = "dde627a8685454f51967cdad798f97f7b3ed0d8b0f33dd9bfc011d98ae81f9ce";
    public static readonly SourceToonShaderVersion Version = new(9, 2, 69);

    public static bool Matches(SourceToonShaderMatchInput input)
    {
        if (!string.Equals(input.ShaderGuid, ShaderGuid, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(input.ShaderSource))
            return false;

        byte[] sourceBytes = Encoding.UTF8.GetBytes(input.ShaderSource);
        string hash = Convert.ToHexString(SHA256.HashData(sourceBytes));
        return string.Equals(hash, ShaderSha256, StringComparison.OrdinalIgnoreCase);
    }
}
