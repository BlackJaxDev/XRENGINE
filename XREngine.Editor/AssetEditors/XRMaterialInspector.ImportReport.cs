using System.Numerics;
using ImGuiNET;
using XREngine.Rendering;
using XREngine.Scene.Importers;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.Editor.AssetEditors;

public sealed partial class XRMaterialInspector
{
    private static void DrawConversionReportWorkspace(XRMaterial material)
    {
        MaterialConversionReport? report =
            material is SerializedMaterialAsset asset && asset.LastConversionReport is not null
                ? asset.LastConversionReport
                : MaterialConversionReportRegistry.Instance.TryGet(material, out MaterialConversionReport found)
                    ? found
                    : null;
        if (report is null)
            return;

        ImGui.Separator();
        ImGui.TextUnformatted("Source conversion");
        ImGui.TextUnformatted($"Original: {report.MaterialName}");
        ImGui.TextUnformatted(
            $"Shader: {report.SourceShaderFamily} {report.SourceShaderVersion} " +
            $"({(report.SourceWasLocked ? "locked" : "unlocked")})");
        ImGui.TextUnformatted(
            $"Status: {report.Outcome} | Converter: {report.ConverterVersion} | " +
            $"Descriptor: {report.SourceDescriptorVersion}");
        ImGui.TextDisabled($"Source SHA-256: {report.SourceContentSha256}");
        ImGui.TextUnformatted(
            $"Features {report.Counters.GeneratedFeatures}/{report.Counters.EnabledSourceFeatures} | " +
            $"Samplers {report.Counters.SamplerPressure} | Variants {report.Counters.GeneratedVariants} | " +
            $"Passes {report.Counters.GeneratedPasses} | Unsupported {report.Counters.UnsupportedIntegrations}");

        if (ImGui.TreeNode("Enabled source feature parity"))
        {
            foreach (IGrouping<string, MaterialFeatureConversionStatus> family in report.Features
                         .Where(static feature => feature.SourceEnabled)
                         .GroupBy(static feature => feature.FeatureFamily)
                         .OrderBy(static group => group.Key, StringComparer.Ordinal))
            {
                if (!ImGui.TreeNode($"{family.Key}##ConversionFamily"))
                    continue;
                foreach (MaterialFeatureConversionStatus feature in family
                             .OrderBy(static value => value.DisplayName, StringComparer.Ordinal))
                {
                    ImGui.TextColored(ResolveParityColor(feature.Parity), $"[{FormatParity(feature.Parity)}]");
                    ImGui.SameLine();
                    ImGui.TextUnformatted(feature.DisplayName);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(
                            $"{feature.NativeEquivalent}\n\nSemantic difference: {feature.SemanticDifference}");
                    }
                }
                ImGui.TreePop();
            }
            ImGui.TreePop();
        }

        if (report.PreservedInactiveValues.Count > 0 &&
            ImGui.TreeNode($"Preserved inactive values ({report.PreservedInactiveValues.Count})"))
        {
            foreach (MaterialPreservedValue value in report.PreservedInactiveValues)
            {
                ImGui.BulletText($"{value.SourceProperty} -> {value.SemanticProperty} ({value.ValueKind})");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{value.Reason}\n{value.SerializedValue}");
            }
            ImGui.TreePop();
        }

        if (report.DiagnosticGroups.Count > 0 &&
            ImGui.TreeNode($"Diagnostics ({report.DiagnosticGroups.Sum(static group => group.Diagnostics.Count)})"))
        {
            foreach (MaterialConversionDiagnosticGroup group in report.DiagnosticGroups)
            {
                if (!ImGui.TreeNode($"{group.MaterialName} / {group.FeatureFamily}##ConversionDiagnostics"))
                    continue;
                foreach (MaterialConversionDiagnostic diagnostic in group.Diagnostics)
                    ImGui.BulletText(diagnostic.ToString());
                ImGui.TreePop();
            }
            ImGui.TreePop();
        }

        if (report.Warnings.Count > 0 && ImGui.TreeNode($"Warnings ({report.Warnings.Count})"))
        {
            foreach (string warning in report.Warnings)
                ImGui.BulletText(warning);
            ImGui.TreePop();
        }
        if (report.Failures.Count > 0 && ImGui.TreeNode($"Failures ({report.Failures.Count})"))
        {
            foreach (string failure in report.Failures)
                ImGui.BulletText(failure);
            ImGui.TreePop();
        }

        if (ImGui.SmallButton("Copy conversion JSON"))
            ImGui.SetClipboardText(report.ToJson());

        if (material is not SerializedMaterialAsset importedAsset)
            return;

        bool needsReimport = MaterialReimportWorkflow.NeedsReimport(importedAsset, out string reimportReason);
        ImGui.TextColored(
            needsReimport ? new Vector4(1.0f, 0.7f, 0.25f, 1.0f) : new Vector4(0.35f, 0.85f, 0.45f, 1.0f),
            needsReimport ? $"Reimport recommended: {reimportReason}" : reimportReason);

        ImGui.SameLine();
        if (ImGui.SmallButton("Reconvert (preserve overrides)"))
        {
            bool succeeded = MaterialReimportWorkflow.Reconvert(importedAsset, out SerializedMaterialImportResult result);
            AuthoringToolStates.GetValue(importedAsset, static _ => new()).Status =
                succeeded
                    ? $"Reconverted; preserved {importedAsset.LocalOverrides.Parameters.Count} parameter override(s)."
                    : result.ConversionReport?.Failures.FirstOrDefault() ?? "Reconversion failed.";
        }

        AuthoringToolState tools = AuthoringToolStates.GetValue(importedAsset, static _ => new());
        ImGui.Checkbox("Confirm reset of local overrides", ref tools.ConfirmConversionReset);
        ImGui.BeginDisabled(!tools.ConfirmConversionReset);
        if (ImGui.SmallButton("Reset overrides and reconvert"))
        {
            bool succeeded = MaterialReimportWorkflow.ResetAndReconvert(
                importedAsset,
                out SerializedMaterialImportResult result);
            tools.Status = succeeded
                ? "Local overrides were reset and source state was reconverted."
                : result.ConversionReport?.Failures.FirstOrDefault() ?? "Reset/reconversion failed.";
            tools.ConfirmConversionReset = false;
        }
        ImGui.EndDisabled();
    }

    private static Vector4 ResolveParityColor(EMaterialFeatureParity parity)
        => parity switch
        {
            EMaterialFeatureParity.Exact => new(0.35f, 0.85f, 0.45f, 1.0f),
            EMaterialFeatureParity.NativeEquivalent => new(0.35f, 0.7f, 1.0f, 1.0f),
            _ => new(1.0f, 0.7f, 0.25f, 1.0f),
        };

    private static string FormatParity(EMaterialFeatureParity parity)
        => parity switch
        {
            EMaterialFeatureParity.NativeEquivalent => "native equivalent",
            EMaterialFeatureParity.PreservedInactive => "preserved inactive",
            _ => "exact",
        };
}
