using System.Text;

namespace XREngine.Tools.ShaderCooker;

/// <summary>Checks the physical resource layout of the selected browser WGSL profile.</summary>
internal static class WgslAbiVerifier
{
    /// <remarks>This is a bounded ABI check, not a WGSL syntax or semantic validator; the browser compiles WGSL.</remarks>
    internal static void Validate(string wgsl, string context)
    {
        if (wgsl is null || Encoding.UTF8.GetByteCount(wgsl) > 1024 * 1024)
            throw new InvalidDataException($"{context}: WGSL exceeds the ABI verifier's 1 MiB limit.");
        new WgslAbiParser(wgsl, context).Validate();
    }
}
