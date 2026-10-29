using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Editor.Publishing;

internal sealed partial class BrowserMaterialCookProjection
{
    /// <summary>Retains the image tint and rejects any active or unknown desktop-only parameter.</summary>
    internal static ShaderVector4 ReadUiImageTint(XRMaterial source, bool hasCanonicalDesktopStage)
    {
        if (source.Parameters.Length == 1 && source.Parameters[0] is ShaderVector4 { Name: "MatColor" } tint)
            return tint;

        // The desktop shader interface discovers uniforms in inactive transparency
        // snippets. Only the complete, unchanged default set can be omitted after
        // the caller has verified the canonical desktop stage and its snippets.
        if (!hasCanonicalDesktopStage || source.Parameters.Length != 5)
            throw InvalidUiImageParameters(source);

        ShaderVector4? color = null;
        int seen = 0;
        foreach (ShaderVar parameter in source.Parameters)
        {
            int bit = parameter switch
            {
                ShaderVector4 { Name: "MatColor" } => 1,
                ShaderUInt { Name: "PpllMaxNodes", Value: 0 } => 2,
                ShaderInt { Name: "DepthPeelLayerIndex", Value: 0 } => 4,
                ShaderFloat { Name: "DepthPeelEpsilon", Value: 0 } => 8,
                ShaderBool { Name: "DepthPeelReversedDepth", Value: false } => 16,
                _ => 0,
            };
            if (bit == 0 || (seen & bit) != 0)
                throw InvalidUiImageParameters(source);
            seen |= bit;
            if (bit == 1)
                color = (ShaderVector4)parameter;
        }
        return seen == 31 && color is not null ? color : throw InvalidUiImageParameters(source);
    }

    private static NotSupportedException InvalidUiImageParameters(XRMaterial source)
        => new($"BrowserCook.UiImageProjectionUnsupported: '{source.Name}' requires MatColor alone or the canonical desktop stage's complete inactive transparency defaults.");
}
