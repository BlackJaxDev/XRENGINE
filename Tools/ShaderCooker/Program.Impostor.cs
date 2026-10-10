using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Tools.ShaderCooker;

internal static partial class Program
{
    private static void VerifyImpostorSources(string sourceRoot, string shaderRoot, bool gate, string selectedSource, string context)
    {
        var sources = EngineOctahedralImpostorShaderContract.CompanionSources(gate);
        Require(selectedSource == sources[0].Path, $"{context}: impostors require the canonical companion entry source.");
        foreach (EngineLitMaterialShaderSource canonical in sources)
        {
            string text = StrictUtf8.GetString(ReadBounded(ResolveInput(sourceRoot, canonical.Path), MaxSourceBytes));
            Require(EngineTexturedAlphaShaderGenerator.NormalizedHash(text) == canonical.Sha256,
                $"{context}: canonical impostor companion '{canonical.Path}' is modified.");
        }
        foreach (EngineLitMaterialShaderSource canonical in EngineOctahedralImpostorShaderContract.DesktopSources)
        {
            string text = StrictUtf8.GetString(ReadBounded(ResolveInput(shaderRoot, canonical.Path), MaxSourceBytes));
            Require(EngineTexturedAlphaShaderGenerator.NormalizedHash(text) == canonical.Sha256,
                $"{context}: canonical desktop impostor source '{canonical.Path}' is modified.");
        }
    }
}
