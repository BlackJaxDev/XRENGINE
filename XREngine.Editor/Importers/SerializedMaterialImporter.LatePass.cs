using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    private static void ApplySourceToonLatePassMetadata(
        XRMaterial material,
        SerializedMaterialDocument document,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        if (material.RenderPass != (int)EDefaultRenderPass.TransparentForward)
            return;

        RenderingParameters options = material.RenderOptions;
        bool requiresSceneColor =
            document.TryGetPositive("_GrabPass") ||
            document.TryGetPositive("_Refraction") ||
            document.TryGetPositive("_Refract");
        string? unsupportedReason = null;
        if (requiresSceneColor)
            unsupportedReason = "The imported Poiyomi scene-color effect has no compatible Advanced late shader.";
        else if (options.BlendModeAllDrawBuffers?.Enabled != ERenderParamUsage.Enabled)
            unsupportedReason = "The source transparent pass does not enable blending.";
        else if (options.AlphaToCoverage == ERenderParamUsage.Enabled)
            unsupportedReason = "The source transparent pass also enables alpha-to-coverage.";
        else if (options.DepthTest?.Enabled != ERenderParamUsage.Enabled)
            unsupportedReason = "The source transparent pass does not enable depth testing.";
        else if (!options.WriteRed && !options.WriteGreen && !options.WriteBlue && !options.WriteAlpha)
            unsupportedReason = "The source transparent pass does not write color.";

        material.AdvancedLatePassMetadata = new AdvancedLatePassMetadata(
            requiresSceneColor ? EAdvancedLatePassKind.Refraction : EAdvancedLatePassKind.SortedAlpha,
            requiresSceneColorSnapshot: requiresSceneColor,
            writesDepth: options.DepthTest?.UpdateDepth == true,
            isOrderDependent: true,
            unsupportedReason: unsupportedReason)
        {
            TemporalUnsupportedReason = "The imported Poiyomi material has no coverage-matching velocity and reactive shader variants for its geometry.",
        };

        diagnostics.Add(new MaterialConversionDiagnostic(
            MaterialConversionDiagnosticCodes.IntentionalNativeDifference,
            MaterialConversionDiagnosticSeverity.Info,
            "Advanced late temporal replay is unavailable until this material has coverage-matching velocity and reactive shader variants for its geometry.",
            "Base"));

        if (unsupportedReason is not null)
        {
            diagnostics.Add(new MaterialConversionDiagnostic(
                MaterialConversionDiagnosticCodes.RenderStateDifference,
                MaterialConversionDiagnosticSeverity.Warning,
                $"Advanced late transparency rejected this source state: {unsupportedReason}",
                "Base"));
        }
    }
}
