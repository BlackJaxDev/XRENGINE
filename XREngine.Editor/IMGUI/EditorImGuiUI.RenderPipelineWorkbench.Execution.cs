using System.Numerics;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static readonly string[] WorkbenchExecutionTabNames = ["Plan", "Barriers", "Lifetimes", "Reorders", "Compare"];
    private static readonly Vector4[] WorkbenchAliasColors =
    [
        new(0.36f, 0.62f, 0.94f, 1f),
        new(0.94f, 0.58f, 0.30f, 1f),
        new(0.45f, 0.80f, 0.45f, 1f),
        new(0.86f, 0.42f, 0.70f, 1f),
        new(0.95f, 0.85f, 0.35f, 1f),
        new(0.45f, 0.85f, 0.85f, 1f),
        new(0.70f, 0.55f, 0.95f, 1f),
        new(0.85f, 0.45f, 0.40f, 1f),
    ];
    private static readonly Vector4 WorkbenchLifetimeColor = new(0.5f, 0.5f, 0.55f, 1f);
    private const float WorkbenchLifetimeNameWidth = 220f;

    /// <summary>
    /// Returns the plan source of the renderer that draws the selected instance, or of the current renderer when the
    /// instance has no window.
    /// </summary>
    private static IRenderGraphPlanDiagnosticsSource? ResolveWorkbenchPlanSource()
    {
        AbstractRenderer? renderer = _workbench.Model.Instance?.LastWindowViewport?.Window?.Renderer ?? AbstractRenderer.Current;
        return renderer?.PlanDiagnostics;
    }

    private static void UpdateWorkbenchPlan()
        => _workbench.Plan.Update(_workbench.Model, ResolveWorkbenchPlanSource());

    private static void DrawWorkbenchPlanBadge()
    {
        UpdateWorkbenchPlan();
        RenderPipelineWorkbenchPlanView plan = _workbench.Plan;
        ImGui.SameLine();
        if (plan.ViolationCount > 0)
            ImGui.TextColored(WorkbenchErrorColor, plan.BadgeText);
        else if (plan.Backend is null)
            ImGui.TextDisabled(plan.BadgeText);
        else
            ImGui.TextColored(WorkbenchBadgeColor, plan.BadgeText);

        if (ImGui.IsItemClicked())
            _workbench.RequestedView = ERenderPipelineWorkbenchView.Execution;
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(plan.SummaryText);
        if (plan.Backend is null)
            ImGui.TextDisabled("The renderer has not published a plan for this instance. The Execution view shows the offline plan.");
        ImGui.TextDisabled("Click to open the Execution view.");
        ImGui.EndTooltip();
    }

    /// <summary>
    /// Draws the Execution view: the plan source, then the Plan, Barriers, Lifetimes, Reorders, and Compare tabs.
    /// </summary>
    private static void DrawWorkbenchExecutionView()
    {
        UpdateWorkbenchPlan();
        RenderPipelineWorkbenchPlanView plan = _workbench.Plan;
        RenderGraphPlanSnapshot? active = plan.Active;
        if (active is null)
        {
            ImGui.TextDisabled("The pipeline has no pass metadata to plan.");
            return;
        }

        ImGui.TextUnformatted(plan.SummaryText);
        if (active.Origin == EPlanSnapshotOrigin.OpenGL)
            ImGui.TextColored(WorkbenchBadgeColor, "OpenGL runs passes in the order that the commands record them. It does not reorder, batch, or insert barriers.");
        else if (plan.Backend is null)
            ImGui.TextColored(WorkbenchWarningColor, "The renderer has not published a plan for this instance. This is the offline plan of the neutral compiler.");
        if (plan.ViolationCount > 0)
            ImGui.TextColored(WorkbenchErrorColor, plan.ViolationText);

        if (ImGui.BeginTabBar("##WorkbenchExecutionTabs"))
        {
            for (int i = 0; i < WorkbenchExecutionTabNames.Length; i++)
            {
                if (ImGui.BeginTabItem(WorkbenchExecutionTabNames[i]))
                {
                    _workbench.ExecutionTab = (ERenderPipelineExecutionTab)i;
                    ImGui.EndTabItem();
                }
            }

            ImGui.EndTabBar();
        }

        switch (_workbench.ExecutionTab)
        {
            case ERenderPipelineExecutionTab.Plan:
                if (active.Origin == EPlanSnapshotOrigin.OpenGL && ImGui.CollapsingHeader("Explicit synchronization commands"))
                {
                    if (plan.SyncItems.Count == 0)
                        ImGui.TextDisabled("The pipeline has no memory barrier or fence commands.");
                    DrawWorkbenchItems(plan.SyncItems);
                }
                DrawWorkbenchPlanRows(plan);
                break;
            case ERenderPipelineExecutionTab.Barriers:
                DrawWorkbenchBarriers(plan, active);
                break;
            case ERenderPipelineExecutionTab.Lifetimes:
                DrawWorkbenchLifetimes(plan, active);
                break;
            case ERenderPipelineExecutionTab.Reorders:
                ImGui.BeginChild("##WorkbenchReorders", Vector2.Zero);
                DrawWorkbenchItems(plan.ReorderItems);
                ImGui.EndChild();
                break;
            case ERenderPipelineExecutionTab.Compare:
                DrawWorkbenchCompare(plan);
                break;
        }
    }

    private static unsafe void DrawWorkbenchPlanRows(RenderPipelineWorkbenchPlanView plan)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##WorkbenchPlanRows", 2, flags, Vector2.Zero))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Order and pass", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableHeadersRow();

        IReadOnlyList<RenderPipelinePlanRow> rows = plan.PlanRows;
        int selectedPass = _workbench.Selection.PassIndex;
        IReadOnlySet<int> linked = _workbench.Links.LinkedPasses;
        ImGuiListClipper clipper = new();
        ImGuiNative.ImGuiListClipper_Begin(&clipper, rows.Count, -1f);
        try
        {
            while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    DrawWorkbenchPlanRow(rows[i], i, selectedPass, linked);
            }
        }
        finally
        {
            ImGuiNative.ImGuiListClipper_End(&clipper);
        }

        ImGui.EndTable();
    }

    private static void DrawWorkbenchPlanRow(in RenderPipelinePlanRow row, int index, int selectedPass, IReadOnlySet<int> linked)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (row.Kind != ERenderPipelinePlanRowKind.Pass)
        {
            if (row.Kind == ERenderPipelinePlanRowKind.Batch)
                ImGui.Indent(12f);
            ImGui.TextColored(WorkbenchBadgeColor, row.Text);
            if (row.Kind == ERenderPipelinePlanRowKind.Batch)
                ImGui.Unindent(12f);
            ImGui.TableNextColumn();
            ImGui.TextDisabled(row.Detail);
            return;
        }

        bool dim = !row.Executed;
        if (dim)
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, WorkbenchSkippedAlpha);

        ImGui.Indent(24f);
        ImGui.PushID(index);
        bool selected = row.PassIndex == selectedPass || linked.Contains(row.PassIndex);
        if (ImGui.Selectable(row.Text, selected, ImGuiSelectableFlags.SpanAllColumns))
            _workbench.Selection.Select(RenderPipelineWorkbenchSelectionEntry.ForPass(row.PassIndex));
        ImGui.PopID();
        ImGui.Unindent(24f);

        ImGui.TableNextColumn();
        if (row.Reordered)
            ImGui.TextColored(WorkbenchWarningColor, row.Detail);
        else
            ImGui.TextDisabled(row.Detail);
        string timingBadge = _workbench.Timing.GetPassBadge(row.PassIndex);
        if (timingBadge.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(WorkbenchBadgeColor, timingBadge);
        }

        if (dim)
            ImGui.PopStyleVar();
    }

    private static unsafe void DrawWorkbenchBarriers(RenderPipelineWorkbenchPlanView plan, RenderGraphPlanSnapshot active)
    {
        if (active.Barriers.Count == 0)
        {
            ImGui.TextDisabled(active.Origin switch
            {
                EPlanSnapshotOrigin.OpenGL => "OpenGL has no explicit barriers. The driver orders memory access.",
                EPlanSnapshotOrigin.OfflineCompile => "The offline plan has no physical barriers. Use the Vulkan renderer to see them.",
                _ => "The plan has no barriers.",
            });
            return;
        }

        DrawWorkbenchBarrierFilters();
        int passFilter = _workbench.BarriersOfSelectedPassOnly ? _workbench.Selection.PassIndex : -1;
        string? resourceFilter = _workbench.BarriersOfSelectedResourceOnly ? _workbench.Selection.ResourceName : null;
        plan.UpdateBarrierFilter(_workbench.BarrierSearch, _workbench.BarrierKinds, passFilter, resourceFilter);

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##WorkbenchBarriers", 6, flags, Vector2.Zero))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Pass", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("Resource", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("Range", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Layout", ImGuiTableColumnFlags.WidthStretch, 1.8f);
        ImGui.TableSetupColumn("Stages", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Access", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableHeadersRow();

        IReadOnlyList<int> visible = plan.VisibleBarriers;
        int selectedPass = _workbench.Selection.PassIndex;
        ImGuiListClipper clipper = new();
        ImGuiNative.ImGuiListClipper_Begin(&clipper, visible.Count, -1f);
        try
        {
            while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    DrawWorkbenchBarrierRow(plan.BarrierRows[visible[i]], visible[i], selectedPass);
            }
        }
        finally
        {
            ImGuiNative.ImGuiListClipper_End(&clipper);
        }

        ImGui.EndTable();
    }

    private static void DrawWorkbenchBarrierFilters()
    {
        string search = _workbench.BarrierSearch;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.InputTextWithHint("##WorkbenchBarrierSearch", "Search pass, resource, layout, or stage", ref search, 128))
            _workbench.BarrierSearch = search;

        ImGui.SameLine();
        DrawWorkbenchBarrierKindToggle("Images", ERenderPipelineBarrierKindFilter.Image);
        ImGui.SameLine();
        DrawWorkbenchBarrierKindToggle("Buffers", ERenderPipelineBarrierKindFilter.Buffer);
        ImGui.SameLine();
        DrawWorkbenchBarrierKindToggle("Swapchain", ERenderPipelineBarrierKindFilter.Swapchain);
        ImGui.SameLine();
        bool passOnly = _workbench.BarriersOfSelectedPassOnly;
        if (ImGui.Checkbox("Selected pass", ref passOnly))
            _workbench.BarriersOfSelectedPassOnly = passOnly;
        ImGui.SameLine();
        bool resourceOnly = _workbench.BarriersOfSelectedResourceOnly;
        if (ImGui.Checkbox("Selected resource", ref resourceOnly))
            _workbench.BarriersOfSelectedResourceOnly = resourceOnly;
    }

    private static void DrawWorkbenchBarrierKindToggle(string label, ERenderPipelineBarrierKindFilter flag)
    {
        bool value = (_workbench.BarrierKinds & flag) != 0;
        if (ImGui.Checkbox(label, ref value))
            _workbench.BarrierKinds = value ? _workbench.BarrierKinds | flag : _workbench.BarrierKinds & ~flag;
    }

    private static void DrawWorkbenchBarrierRow(in RenderPipelinePlanBarrierRow row, int index, int selectedPass)
    {
        PlanBarrierSnapshot barrier = row.Barrier;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.PushID(index);
        if (ImGui.Selectable(row.PassText, barrier.PassIndex == selectedPass, ImGuiSelectableFlags.SpanAllColumns))
            _workbench.Selection.Select(RenderPipelineWorkbenchSelectionEntry.ForPass(barrier.PassIndex));
        if (ImGui.IsItemHovered() && barrier.RawText.Length > 0)
            ImGui.SetTooltip(barrier.RawText);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            _workbench.Selection.Select(RenderPipelineWorkbenchSelectionEntry.ForResource(barrier.ResourceName));
        ImGui.PopID();

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(barrier.ResourceName);
        ImGui.TableNextColumn();
        ImGui.TextDisabled(barrier.Range);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.LayoutText);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.StageText);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(row.AccessText);
    }

    private static unsafe void DrawWorkbenchLifetimes(RenderPipelineWorkbenchPlanView plan, RenderGraphPlanSnapshot active)
    {
        IReadOnlyList<RenderPipelinePlanLifetimeRow> rows = plan.LifetimeRows;
        if (rows.Count == 0)
        {
            ImGui.TextDisabled("The plan has no resource lifetimes.");
            return;
        }

        if (active.AliasGroups.Count == 0)
            ImGui.TextDisabled("Gray bars do not share memory. This plan has no alias groups.");
        else
            ImGui.TextDisabled("Bars with the same color share one memory allocation. Gray bars do not share memory.");

        if (!ImGui.BeginChild("##WorkbenchLifetimes", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        string? selectedResource = _workbench.Selection.ResourceName;
        float rowHeight = ImGui.GetTextLineHeightWithSpacing();
        float barWidth = MathF.Max(ImGui.GetContentRegionAvail().X - WorkbenchLifetimeNameWidth, 120f);
        float step = barWidth / plan.LifetimeSpan;
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint gridColor = ImGui.GetColorU32(ImGuiCol.Separator, 0.35f);

        ImGuiListClipper clipper = new();
        ImGuiNative.ImGuiListClipper_Begin(&clipper, rows.Count, rowHeight);
        try
        {
            while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    RenderPipelinePlanLifetimeRow row = rows[i];
                    Vector2 origin = ImGui.GetCursorScreenPos();
                    ImGui.PushID(i);
                    bool selected = selectedResource is not null && RenderPipelineWorkbenchLinks.NameMatches(row.Name, selectedResource);
                    if (ImGui.Selectable(row.Name, selected, ImGuiSelectableFlags.None, new Vector2(WorkbenchLifetimeNameWidth - 8f, rowHeight - 2f)))
                        _workbench.Selection.Select(RenderPipelineWorkbenchSelectionEntry.ForResource(row.Name));
                    ImGui.PopID();

                    float barStart = origin.X + WorkbenchLifetimeNameWidth;
                    drawList.AddLine(new Vector2(barStart, origin.Y + rowHeight - 1f), new Vector2(barStart + barWidth, origin.Y + rowHeight - 1f), gridColor);
                    Vector4 color = row.AliasGroup >= 0 ? WorkbenchAliasColors[row.AliasGroup % WorkbenchAliasColors.Length] : WorkbenchLifetimeColor;
                    Vector2 min = new(barStart + row.FirstOrder * step, origin.Y + 2f);
                    Vector2 max = new(barStart + (row.LastOrder + 1) * step, origin.Y + rowHeight - 3f);
                    drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(selected ? color : color with { W = 0.75f }), 2f);
                    if (ImGui.IsMouseHoveringRect(min, max))
                        ImGui.SetTooltip(row.Tooltip);
                }
            }
        }
        finally
        {
            ImGuiNative.ImGuiListClipper_End(&clipper);
        }

        ImGui.EndChild();
    }

    private static unsafe void DrawWorkbenchCompare(RenderPipelineWorkbenchPlanView plan)
    {
        if (plan.UnmappedItems.Count > 0)
        {
            ImGui.TextColored(WorkbenchErrorColor, "These commands do GPU work but declare no pass. The backend plan does not order or synchronize them.");
            DrawWorkbenchItems(plan.UnmappedItems);
            ImGui.Separator();
        }

        string backendLabel = plan.Backend is null ? "Offline" : "Backend";
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##WorkbenchCompare", 4, flags, Vector2.Zero))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Pass", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Authored", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(backendLabel, ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Change", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();

        IReadOnlyList<RenderPipelinePlanCompareRow> rows = plan.CompareRows;
        int selectedPass = _workbench.Selection.PassIndex;
        ImGuiListClipper clipper = new();
        ImGuiNative.ImGuiListClipper_Begin(&clipper, rows.Count, -1f);
        try
        {
            while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    RenderPipelinePlanCompareRow row = rows[i];
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (!row.Executed)
                        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, WorkbenchSkippedAlpha);
                    ImGui.PushID(i);
                    if (ImGui.Selectable(row.Name, row.PassIndex == selectedPass, ImGuiSelectableFlags.SpanAllColumns))
                        _workbench.Selection.Select(RenderPipelineWorkbenchSelectionEntry.ForPass(row.PassIndex));
                    ImGui.PopID();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.AuthoredText);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.BackendText);
                    ImGui.TableNextColumn();
                    if (row.Moved)
                        ImGui.TextColored(WorkbenchWarningColor, row.DeltaText);
                    if (!row.Executed)
                        ImGui.PopStyleVar();
                }
            }
        }
        finally
        {
            ImGuiNative.ImGuiListClipper_End(&clipper);
        }

        ImGui.EndTable();
    }
}
