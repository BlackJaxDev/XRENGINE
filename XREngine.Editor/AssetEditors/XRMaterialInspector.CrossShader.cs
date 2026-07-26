using System.Runtime.CompilerServices;
using ImGuiNET;
using XREngine.Editor.MaterialAuthoring;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Editor.AssetEditors;

public sealed partial class XRMaterialInspector
{
    private static readonly ConditionalWeakTable<XRMaterial, CrossShaderEditorState> CrossShaderStates = new();

    private static void DrawMultiMaterialAuthoringInspector(IReadOnlyList<object> rawTargets)
    {
        List<XRMaterial> selected = [];
        foreach (object target in rawTargets)
            if (target is XRMaterial material)
                selected.Add(material);
        if (selected.Count < 2 || selected.Count != rawTargets.Count)
        {
            ImGui.TextDisabled("Cross-Shader editing requires at least two material-only targets.");
            return;
        }

        XRMaterial selectionPrimary = selected[0];
        CrossShaderEditorState state = CrossShaderStates.GetValue(selectionPrimary, _ => new(selected));
        DrawHeader(selectionPrimary);
        ImGui.TextColored(AuthoringGroupColor, $"Cross-Shader Material Editor — {state.Materials.Count} materials");
        ImGui.TextDisabled("Edits are matched by stable semantic ID and committed as one transaction.");

        if (ImGui.SmallButton("Refresh from selection"))
            state.SetMaterials(selected);
        ImGui.SameLine();
        ImGui.Checkbox("Union view", ref state.UnionView);
        ImGui.SameLine();
        if (ImGui.SmallButton("Prepare all"))
        {
            int succeeded = 0;
            foreach (XRMaterial material in state.Materials)
                if (material.PrepareUberVariantImmediately())
                    succeeded++;
            state.Status = $"Prepared {succeeded}/{state.Materials.Count} variants.";
        }

        for (int index = 0; index < state.Materials.Count; index++)
        {
            XRMaterial material = state.Materials[index];
            ImGui.PushID(RuntimeHelpers.GetHashCode(material));
            ImGui.TextUnformatted($"{index + 1}. {material.Name ?? "Material"}");
            if (index > 0)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                {
                    state.Materials.RemoveAt(index);
                    index--;
                }
            }
            ImGui.PopID();
        }

        List<CrossMaterialTarget> targets = [];
        foreach (XRMaterial material in state.Materials)
        {
            ShaderAuthoringSchema? schema = null;
            if (TryGetUberMaterialManifest(material, out _, out _, out ShaderUiManifest? manifest) &&
                manifest is not null)
                schema = PoiyomiAuthoringSchemaCatalog.GetOrCreate(manifest);
            targets.Add(new(material, schema));
        }

        List<ShaderAuthoringNode> nodes = BuildCrossShaderNodeList(targets, state.UnionView);
        if (!ImGui.BeginTable(
                "CrossShaderSemanticProperties",
                5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
                new System.Numerics.Vector2(0.0f, 520.0f)))
            return;

        ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthStretch, 0.38f);
        ImGui.TableSetupColumn("Accepts", ImGuiTableColumnFlags.WidthFixed, 72.0f);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 72.0f);
        ImGui.TableSetupColumn("Representative", ImGuiTableColumnFlags.WidthStretch, 0.30f);
        ImGui.TableSetupColumn("Batch", ImGuiTableColumnFlags.WidthFixed, 112.0f);
        ImGui.TableHeadersRow();

        foreach (ShaderAuthoringNode node in nodes)
        {
            if (!TryFindRepresentative(targets, node.SemanticId, out CrossMaterialValue representative))
                continue;
            List<CrossMaterialValue> compatible = ResolveCrossMaterialValues(targets, representative);
            int same = compatible.Count(value =>
                string.Equals(value.SerializedValue, representative.SerializedValue, StringComparison.Ordinal));

            ImGui.PushID(node.SemanticId);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(node.DisplayName);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{node.SemanticId}\n{compatible.Count}/{targets.Count} compatible material(s)");
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted($"{compatible.Count}/{targets.Count}");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextColored(
                same == compatible.Count ? EngineActiveColor : UberFeatureRequestedColor,
                same == compatible.Count ? "Same" : "Mixed");
            ImGui.TableSetColumnIndex(3);
            ImGui.TextDisabled(representative.SerializedValue);
            ImGui.TableSetColumnIndex(4);
            using (new ImGuiDisabledScope(compatible.Count < 2 || same == compatible.Count))
            if (ImGui.SmallButton("Use first"))
                ApplyCrossShaderValue(compatible, representative, out state.Status);
            ImGui.PopID();
        }

        ImGui.EndTable();
        if (!string.IsNullOrWhiteSpace(state.Status))
            ImGui.TextWrapped(state.Status);
    }

    private static List<ShaderAuthoringNode> BuildCrossShaderNodeList(
        IReadOnlyList<CrossMaterialTarget> targets,
        bool union)
    {
        Dictionary<string, (ShaderAuthoringNode Node, int Count)> nodes = new(StringComparer.Ordinal);
        foreach (CrossMaterialTarget target in targets)
        {
            if (target.Schema is null)
                continue;
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (ShaderAuthoringNode node in target.Schema.DeclarationOrder)
            {
                if (node.ManifestProperty is not { IsSampler: false } || !seen.Add(node.SemanticId))
                    continue;
                if (nodes.TryGetValue(node.SemanticId, out (ShaderAuthoringNode Node, int Count) existing))
                    nodes[node.SemanticId] = (existing.Node, existing.Count + 1);
                else
                    nodes[node.SemanticId] = (node, 1);
            }
        }

        IEnumerable<(ShaderAuthoringNode Node, int Count)> filtered = union
            ? nodes.Values
            : nodes.Values.Where(value => value.Count == targets.Count);
        return filtered
            .OrderBy(static value => value.Node.DeclarationOrder)
            .ThenBy(static value => value.Node.SemanticId, StringComparer.Ordinal)
            .Select(static value => value.Node)
            .ToList();
    }

    private static bool TryFindRepresentative(
        IReadOnlyList<CrossMaterialTarget> targets,
        string semanticId,
        out CrossMaterialValue representative)
    {
        foreach (CrossMaterialTarget target in targets)
        {
            if (TryResolveCrossMaterialValue(target, semanticId, null, out representative))
                return true;
        }
        representative = default;
        return false;
    }

    private static List<CrossMaterialValue> ResolveCrossMaterialValues(
        IReadOnlyList<CrossMaterialTarget> targets,
        CrossMaterialValue representative)
    {
        List<CrossMaterialValue> values = [];
        foreach (CrossMaterialTarget target in targets)
            if (TryResolveCrossMaterialValue(
                    target,
                    representative.Node.SemanticId,
                    representative.Property.GlslType,
                    out CrossMaterialValue value))
                values.Add(value);
        return values;
    }

    private static bool TryResolveCrossMaterialValue(
        CrossMaterialTarget target,
        string semanticId,
        string? requiredType,
        out CrossMaterialValue value)
    {
        value = default;
        if (target.Schema is null ||
            !target.Schema.NodeLookup.TryGetValue(semanticId, out ShaderAuthoringNode? node) ||
            node.ManifestProperty is not ShaderUiProperty property ||
            property.IsSampler ||
            (requiredType is not null &&
             !string.Equals(requiredType, property.GlslType, StringComparison.Ordinal)))
            return false;
        ShaderVar? parameter = FindMaterialParameter(target.Material, property.Name);
        if (parameter is null || !TrySerializeShaderParameterValue(parameter, out string serialized))
            return false;
        value = new(target.Material, node, property, parameter, serialized);
        return true;
    }

    private static bool ApplyCrossShaderValue(
        IReadOnlyList<CrossMaterialValue> targets,
        CrossMaterialValue source,
        out string status)
    {
        MaterialAuthoringTransaction transaction = new($"Set {source.Node.DisplayName} on materials");
        foreach (CrossMaterialValue target in targets)
        {
            CrossMaterialValue captured = target;
            transaction.Add(
                captured.Material,
                captured.Node.DisplayName,
                () => CanApplyShaderParameterClipboard(captured.Parameter, source.SerializedValue)
                    ? null
                    : "The semantic value contract changed.",
                () =>
                {
                    TryApplyShaderParameterClipboard(
                        captured.Material,
                        captured.Parameter,
                        source.SerializedValue);
                    EShaderUiPropertyMode mode = captured.Material.GetUberPropertyMode(
                        captured.Property.Name,
                        captured.Property.DefaultMode,
                        false);
                    if (mode == EShaderUiPropertyMode.Static)
                        captured.Material.RefreshUberPropertyStaticLiteral(captured.Property.Name);
                },
                true);
        }
        bool succeeded = transaction.TryExecute(out MaterialAuthoringTransactionReport report);
        status = succeeded
            ? $"Updated {targets.Count} compatible material(s) in one transaction."
            : string.Join("; ", report.Diagnostics);
        return succeeded;
    }

    private sealed class CrossShaderEditorState(IReadOnlyList<XRMaterial> materials)
    {
        public readonly List<XRMaterial> Materials = [.. materials];
        public bool UnionView = true;
        public string? Status;

        public void SetMaterials(IReadOnlyList<XRMaterial> materials)
        {
            Materials.Clear();
            foreach (XRMaterial material in materials)
                if (!Materials.Contains(material, ReferenceEqualityComparer.Instance))
                    Materials.Add(material);
        }
    }

    private sealed record CrossMaterialTarget(XRMaterial Material, ShaderAuthoringSchema? Schema);

    private readonly record struct CrossMaterialValue(
        XRMaterial Material,
        ShaderAuthoringNode Node,
        ShaderUiProperty Property,
        ShaderVar Parameter,
        string SerializedValue);
}
