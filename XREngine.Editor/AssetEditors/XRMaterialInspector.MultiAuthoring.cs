using ImGuiNET;
using XREngine.Editor.MaterialAuthoring;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Editor.AssetEditors;

public sealed partial class XRMaterialInspector
{
    private static void DrawLegacyMultiMaterialAuthoringInspector(IReadOnlyList<object> rawTargets)
    {
        List<XRMaterial> materials = new(rawTargets.Count);
        foreach (object target in rawTargets)
        {
            if (target is XRMaterial material)
                materials.Add(material);
        }

        if (materials.Count != rawTargets.Count || materials.Count < 2)
        {
            ImGui.TextDisabled("Multi-material authoring requires material-only selection.");
            return;
        }

        XRMaterial primary = materials[0];
        DrawHeader(primary);
        ImGui.TextColored(AuthoringGroupColor, $"Cross-Shader Material Editor — {materials.Count} materials");
        ImGui.TextDisabled("Properties are matched by stable semantic ID. Incompatible shaders are reported and skipped.");

        if (ImGui.Button("Prepare All Variants"))
        {
            int succeeded = 0;
            foreach (XRMaterial material in materials)
                if (material.PrepareUberVariantImmediately())
                    succeeded++;
            ImGui.SetTooltip($"Prepared {succeeded} of {materials.Count} material variants.");
        }

        if (!TryGetUberMaterialManifest(primary, out _, out _, out ShaderUiManifest? primaryManifest) ||
            primaryManifest is null)
        {
            ImGui.TextDisabled("The primary material has no authoring manifest.");
            return;
        }

        ShaderAuthoringSchema primarySchema = PoiyomiAuthoringSchemaCatalog.GetOrCreate(primaryManifest);
        List<MultiMaterialSchema> targets = new(materials.Count);
        foreach (XRMaterial material in materials)
        {
            if (!TryGetUberMaterialManifest(material, out _, out _, out ShaderUiManifest? manifest) ||
                manifest is null)
            {
                targets.Add(new(material, null));
                continue;
            }
            targets.Add(new(material, PoiyomiAuthoringSchemaCatalog.GetOrCreate(manifest)));
        }

        if (!ImGui.BeginTable(
            "MultiMaterialSemanticProperties",
            5,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
            new System.Numerics.Vector2(0.0f, 520.0f)))
            return;

        ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthStretch, 0.38f);
        ImGui.TableSetupColumn("Compatible", ImGuiTableColumnFlags.WidthFixed, 86.0f);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 86.0f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.28f);
        ImGui.TableSetupColumn("Batch", ImGuiTableColumnFlags.WidthFixed, 128.0f);
        ImGui.TableHeadersRow();

        foreach (ShaderAuthoringNode node in primarySchema.DeclarationOrder)
        {
            ShaderUiProperty? property = node.ManifestProperty;
            if (property is null || property.IsSampler)
                continue;

            ShaderVar? primaryParameter = FindMaterialParameter(primary, property.Name);
            if (primaryParameter is null ||
                !TrySerializeShaderParameterValue(primaryParameter, out string serializedPrimary))
                continue;

            int compatible = 0;
            int same = 0;
            foreach (MultiMaterialSchema target in targets)
            {
                if (!TryResolveCompatibleParameter(target, node, property, out ShaderVar? parameter) || parameter is null)
                    continue;
                compatible++;
                if (TrySerializeShaderParameterValue(parameter, out string serialized) &&
                    string.Equals(serialized, serializedPrimary, StringComparison.Ordinal))
                    same++;
            }

            ImGui.PushID(node.SemanticId);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(node.DisplayName);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{node.SemanticId}\nSource: {node.SourcePropertyName}");
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"{compatible}/{materials.Count}");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextColored(
                same == compatible ? EngineActiveColor : UberFeatureRequestedColor,
                same == compatible ? "Same" : "Mixed");
            ImGui.TableSetColumnIndex(3);
            ImGui.TextDisabled(serializedPrimary);
            ImGui.TableSetColumnIndex(4);
            using (new ImGuiDisabledScope(compatible < 2 || same == compatible))
            if (ImGui.SmallButton("Use Primary"))
                ApplyPrimaryValueToCompatibleTargets(targets, node, property, serializedPrimary);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static void ApplyPrimaryValueToCompatibleTargets(
        IReadOnlyList<MultiMaterialSchema> targets,
        ShaderAuthoringNode sourceNode,
        ShaderUiProperty sourceProperty,
        string serializedValue)
    {
        MaterialAuthoringTransaction transaction = new($"Set {sourceNode.DisplayName} on materials");
        foreach (MultiMaterialSchema target in targets)
        {
            if (!TryResolveCompatibleParameter(target, sourceNode, sourceProperty, out ShaderVar? parameter) || parameter is null)
                continue;

            XRMaterial material = target.Material;
            ShaderVar captured = parameter;
            transaction.Add(
                material,
                sourceNode.DisplayName,
                () => CanApplyShaderParameterClipboard(captured, serializedValue)
                    ? null
                    : "The source value is incompatible with this material.",
                () =>
                {
                    TryApplyShaderParameterClipboard(material, captured, serializedValue);
                    EShaderUiPropertyMode mode = material.GetUberPropertyMode(
                        sourceProperty.Name,
                        sourceProperty.DefaultMode,
                        false);
                    if (mode == EShaderUiPropertyMode.Static)
                        material.RefreshUberPropertyStaticLiteral(sourceProperty.Name);
                },
                invalidatesVariant: true);
        }
        transaction.TryExecute(out _);
    }

    private static bool TryResolveCompatibleParameter(
        MultiMaterialSchema target,
        ShaderAuthoringNode sourceNode,
        ShaderUiProperty sourceProperty,
        out ShaderVar? parameter)
    {
        parameter = null;
        if (target.Schema is null ||
            sourceNode.SourcePropertyName is null ||
            !target.Schema.PropertyLookup.TryGetValue(sourceNode.SourcePropertyName, out ShaderAuthoringNode? targetNode) ||
            targetNode.ManifestProperty is not ShaderUiProperty targetProperty ||
            !string.Equals(targetProperty.GlslType, sourceProperty.GlslType, StringComparison.Ordinal))
            return false;

        parameter = FindMaterialParameter(target.Material, targetProperty.Name);
        return parameter is not null;
    }

    private static ShaderVar? FindMaterialParameter(XRMaterial material, string name)
    {
        foreach (ShaderVar parameter in material.Parameters)
            if (string.Equals(parameter.Name, name, StringComparison.Ordinal))
                return parameter;
        return null;
    }

    private sealed record MultiMaterialSchema(
        XRMaterial Material,
        ShaderAuthoringSchema? Schema);
}
