using System.Numerics;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering.Resources.Diagnostics;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static readonly string[] WorkbenchResourceGroupingNames = ["No grouping", "Group by kind", "Group by lifetime"];
    private static readonly string[] WorkbenchResourceDetailTabNames = ["Spec", "Generations", "Usage", "Physical"];

    /// <summary>
    /// Draws the Resources view: the byte summary, the filters, the resource table, and the detail tabs of the selected resource.
    /// </summary>
    private static void DrawWorkbenchResourcesView()
    {
        RenderPipelineWorkbenchModel model = _workbench.Model;
        bool editing = IsWorkbenchEditing(model.Pipeline);
        if (editing)
            DrawWorkbenchResourceAuthoring();
        else if (model.Instance is null)
        {
            ImGui.TextDisabled("Select a pipeline instance. Resources belong to the generation of an instance.");
            return;
        }

        RenderPipelineWorkbenchResourceTable table = _workbench.ResourceTable;
        table.Update(model.Resources, _workbench.ResourceSearch, _workbench.ResourceFilter, _workbench.ResourceGrouping);

        ImGui.TextUnformatted(table.SummaryText);
        if (table.ProblemText.Length > 0)
            ImGui.TextColored(WorkbenchWarningColor, table.ProblemText);

        DrawWorkbenchResourceFilters();

        string? selectedName = _workbench.Selection.ResourceName;
        bool hasDetail = selectedName is not null;
        float available = ImGui.GetContentRegionAvail().Y;
        float tableHeight = hasDetail ? MathF.Max(120f, available * 0.55f) : available;
        DrawWorkbenchResourceTable(table, selectedName, tableHeight);

        if (!hasDetail)
            return;

        RenderPipelineWorkbenchResourceDetail detail = _workbench.ResourceDetail;
        detail.Update(model, selectedName);
        if (ImGui.BeginTabBar("##WorkbenchResourceDetailTabs"))
        {
            for (int i = 0; i < WorkbenchResourceDetailTabNames.Length; i++)
            {
                if (ImGui.BeginTabItem(WorkbenchResourceDetailTabNames[i]))
                {
                    _workbench.ResourceDetailTab = (ERenderPipelineResourceDetailTab)i;
                    ImGui.EndTabItem();
                }
            }

            ImGui.EndTabBar();
        }

        ImGui.BeginChild("##WorkbenchResourceDetail", Vector2.Zero);
        DrawWorkbenchItems(detail.GetTab(_workbench.ResourceDetailTab));
        ImGui.EndChild();
    }

    private static void DrawWorkbenchResourceFilters()
    {
        string search = _workbench.ResourceSearch;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.InputTextWithHint("##WorkbenchResourceSearch", "Search name, kind, or format", ref search, 128))
            _workbench.ResourceSearch = search;

        ImGui.SameLine();
        int grouping = (int)_workbench.ResourceGrouping;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.Combo("##WorkbenchResourceGrouping", ref grouping, WorkbenchResourceGroupingNames, WorkbenchResourceGroupingNames.Length))
            _workbench.ResourceGrouping = (ERenderPipelineResourceGrouping)grouping;

        DrawWorkbenchResourceFilterToggle("Unused", ERenderPipelineResourceFilter.DeclaredButUnused, "Declared but no pass uses it.");
        ImGui.SameLine();
        DrawWorkbenchResourceFilterToggle("Undeclared", ERenderPipelineResourceFilter.UsedButUndeclared, "A pass uses it but the layout does not declare it.");
        ImGui.SameLine();
        DrawWorkbenchResourceFilterToggle("History", ERenderPipelineResourceFilter.History, "Keeps content across frames.");
        ImGui.SameLine();
        DrawWorkbenchResourceFilterToggle("External", ERenderPipelineResourceFilter.External, "Comes from outside the pipeline.");
        ImGui.SameLine();
        DrawWorkbenchResourceFilterToggle("Aliased", ERenderPipelineResourceFilter.Aliased, "The descriptor allows memory aliasing.");
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Flags: H history, X external, T transient, A alias candidate, M materialized, R required, D disabled, U unused, ? undeclared.");
    }

    private static void DrawWorkbenchResourceFilterToggle(string label, ERenderPipelineResourceFilter flag, string tooltip)
    {
        bool value = (_workbench.ResourceFilter & flag) != 0;
        if (ImGui.Checkbox(label, ref value))
            _workbench.ResourceFilter = value ? _workbench.ResourceFilter | flag : _workbench.ResourceFilter & ~flag;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private static unsafe void DrawWorkbenchResourceTable(RenderPipelineWorkbenchResourceTable table, string? selectedName, float height)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##WorkbenchResources", 8, flags, new Vector2(0f, height)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Lifetime", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Format", ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn("Memory", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("W / R  Span", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Flags", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableHeadersRow();

        IReadOnlyList<int> visible = table.Visible;
        if (_workbench.ResourceGrouping == ERenderPipelineResourceGrouping.None)
        {
            ImGuiListClipper clipper = new();
            ImGuiNative.ImGuiListClipper_Begin(&clipper, visible.Count, -1f);
            try
            {
                while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
                    for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        DrawWorkbenchResourceRow(table.Rows[visible[i]], visible[i], selectedName);
            }
            finally
            {
                ImGuiNative.ImGuiListClipper_End(&clipper);
            }
        }
        else
        {
            // Group header rows change the row count, so grouped tables draw every row. Resource counts stay small.
            string? group = null;
            for (int i = 0; i < visible.Count; i++)
            {
                RenderPipelineWorkbenchResourceRow row = table.Rows[visible[i]];
                if (!string.Equals(group, row.GroupText, StringComparison.Ordinal))
                {
                    group = row.GroupText;
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextColored(WorkbenchBadgeColor, group);
                }

                DrawWorkbenchResourceRow(row, visible[i], selectedName);
            }
        }

        ImGui.EndTable();
    }

    private static void DrawWorkbenchResourceRow(RenderPipelineWorkbenchResourceRow row, int rowIndex, string? selectedName)
    {
        RenderPipelineResourceEntry entry = row.Entry;
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.PushID(rowIndex);
        bool selected = selectedName is not null && string.Equals(selectedName, entry.Name, StringComparison.OrdinalIgnoreCase);
        bool problem = entry.IsDeclaredButUnused || entry.IsUsedButUndeclared;
        bool disabled = entry.Has(ERenderPipelineResourceFlags.Disabled);
        if (disabled)
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, WorkbenchSkippedAlpha);
        if (problem)
            ImGui.PushStyleColor(ImGuiCol.Text, WorkbenchWarningColor);
        if (ImGui.Selectable(entry.Name, selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap))
            _workbench.Selection.Select(RenderPipelineWorkbenchSelectionEntry.ForResource(entry.Name));
        if (problem)
            ImGui.PopStyleColor();
        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(entry.Spec?.DebugLabel))
            ImGui.SetTooltip(entry.Spec!.DebugLabel);

        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(row.KindText);
        ImGui.TableSetColumnIndex(2);
        ImGui.TextUnformatted(row.LifetimeText);
        ImGui.TableSetColumnIndex(3);
        ImGui.TextUnformatted(row.SizeText);
        ImGui.TableSetColumnIndex(4);
        ImGui.TextUnformatted(entry.FormatText);
        ImGui.TableSetColumnIndex(5);
        ImGui.TextUnformatted(row.BytesText);
        ImGui.TableSetColumnIndex(6);
        ImGui.TextUnformatted(row.UsesText);
        ImGui.SameLine();
        ImGui.TextDisabled(row.SpanText);
        ImGui.TableSetColumnIndex(7);
        ImGui.TextUnformatted(row.FlagsText);
        if (disabled)
            ImGui.PopStyleVar();
        ImGui.PopID();
    }
}
