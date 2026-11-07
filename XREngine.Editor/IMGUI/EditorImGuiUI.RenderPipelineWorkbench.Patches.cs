using System.Globalization;
using System.Numerics;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Overlays;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static readonly Vector4 WorkbenchOverlayColor = new(0.75f, 0.6f, 0.95f, 1f);

    /// <summary>One cached row of the Patches view.</summary>
    private readonly record struct WorkbenchPatchRow(int OverlayIndex, object Patch, string Text, string Status, ERenderPipelinePatchStatus StatusKind, string Message);

    private static readonly List<WorkbenchPatchRow> _workbenchPatchRows = [];
    private static readonly List<bool> _workbenchOverlayNeedsRebase = [];
    private static object? _workbenchPatchRowsOverlays;
    private static object? _workbenchPatchRowsReport;
    private static string _workbenchDerivedBadge = string.Empty;
    private static object? _workbenchDerivedBadgeKey;

    /// <summary>Draws the derived badge of the target bar: the base type and the overlay count. A click opens the Patches view.</summary>
    private static void DrawWorkbenchDerivedBadge(RenderPipeline pipeline)
    {
        if (!pipeline.IsDerived)
            return;

        if (!ReferenceEquals(_workbenchDerivedBadgeKey, pipeline.Overlays))
        {
            _workbenchDerivedBadgeKey = pipeline.Overlays;
            _workbenchDerivedBadge = string.Concat("Derived from ", pipeline.GetType().Name, " (",
                pipeline.Overlays.Count.ToString(CultureInfo.InvariantCulture), pipeline.Overlays.Count == 1 ? " overlay)" : " overlays)");
        }

        ImGui.SameLine();
        ImGui.TextColored(pipeline.OverlayReport.HasErrors ? WorkbenchErrorColor : WorkbenchOverlayColor, _workbenchDerivedBadge);
        if (ImGui.IsItemClicked())
            _workbench.RequestedView = ERenderPipelineWorkbenchView.Patches;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(pipeline.OverlayReport.HasErrors
                ? "A patch did not apply, so the base renders. Click to open the Patches view."
                : "Click to open the Patches view.");
    }

    /// <summary>Gets the short marker text of a patch kind for the Outline and the Flow.</summary>
    private static string GetWorkbenchPatchMarker(ERenderCommandPatchKind kind)
        => kind switch
        {
            ERenderCommandPatchKind.InsertBefore or ERenderCommandPatchKind.InsertAfter or ERenderCommandPatchKind.AppendTo => "+",
            ERenderCommandPatchKind.Replace => "R",
            ERenderCommandPatchKind.Wrap => "W",
            ERenderCommandPatchKind.SetProperty => "P",
            ERenderCommandPatchKind.SetCondition => "C",
            _ => "*",
        };

    /// <summary>Gets the tooltip of a patch marker.</summary>
    private static string GetWorkbenchPatchMarkerTooltip(ERenderCommandPatchKind kind)
        => kind switch
        {
            ERenderCommandPatchKind.InsertBefore or ERenderCommandPatchKind.InsertAfter or ERenderCommandPatchKind.AppendTo => "An overlay patch added this command.",
            ERenderCommandPatchKind.Replace => "An overlay patch replaced the base command with this command.",
            ERenderCommandPatchKind.Wrap => "An overlay patch added this wrap around base commands.",
            ERenderCommandPatchKind.SetProperty => "An overlay patch changed a property of this command.",
            ERenderCommandPatchKind.SetCondition => "An overlay patch changed the condition of this branch.",
            _ => "An overlay patch changed this command.",
        };

    /// <summary>Draws the overlay marker of <paramref name="command"/> on the current line when a patch changed it.</summary>
    private static void DrawWorkbenchPatchMarker(RenderPipeline? pipeline, Rendering.Pipelines.Commands.ViewportRenderCommand command)
    {
        if (pipeline is null || !pipeline.IsDerived || !pipeline.OverlayReport.Markers.TryGetValue(command, out ERenderCommandPatchKind kind))
            return;

        ImGui.SameLine();
        ImGui.TextColored(WorkbenchOverlayColor, GetWorkbenchPatchMarker(kind));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(GetWorkbenchPatchMarkerTooltip(kind));
    }

    /// <summary>Draws the context menu of a command row: copy the anchor id or the patch target.</summary>
    private static void DrawWorkbenchCommandContextMenu(RenderPipeline? pipeline, Rendering.Pipelines.Commands.ViewportRenderCommand command)
    {
        if (!ImGui.BeginPopupContextItem("##WorkbenchCommandContext"))
            return;

        ImGui.BeginDisabled(command.AnchorId is null);
        if (ImGui.MenuItem("Copy anchor id"))
            ImGui.SetClipboardText(command.AnchorId ?? string.Empty);
        ImGui.EndDisabled();
        if (command.AnchorId is null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("This command has no anchor id. Use Copy patch target for a structural path.");

        if (pipeline is not null && ImGui.MenuItem("Copy patch target"))
            ImGui.SetClipboardText(RenderCommandPaths.GetTarget(pipeline.CommandChain, command));

        ImGui.EndPopup();
    }

    /// <summary>
    /// Draws the Patches view of a derived pipeline: each overlay with its patches and their status. Revert removes a
    /// patch. Accept rebase stores the current base hash and the renamed anchor ids.
    /// </summary>
    private static void DrawWorkbenchPatchesView()
    {
        RenderPipeline? pipeline = _workbench.Model.Pipeline;
        if (pipeline is null || !pipeline.IsDerived)
        {
            ImGui.TextDisabled("The pipeline has no overlays. Use Derive to make a derived pipeline.");
            return;
        }

        UpdateWorkbenchPatchRows(pipeline);
        IReadOnlyList<RenderPipelineOverlay> overlays = pipeline.Overlays;
        if (pipeline.OverlayReport.HasErrors)
            ImGui.TextColored(WorkbenchErrorColor, "A patch did not apply. The base renders the part that holds it.");

        IReadOnlyList<RenderPipelineOverlay>? replacement = null;
        for (int o = 0; o < overlays.Count; o++)
        {
            RenderPipelineOverlay overlay = overlays[o];
            ImGui.PushID(o);
            ImGui.SeparatorText(overlay.Name.Length > 0 ? overlay.Name : "Overlay");
            if (overlay.ParentPath is { Length: > 0 } parentPath)
            {
                ImGui.TextDisabled("Parent:");
                ImGui.SameLine();
                ImGui.TextUnformatted(parentPath);
            }

            if (o < _workbenchOverlayNeedsRebase.Count && _workbenchOverlayNeedsRebase[o])
            {
                ImGui.TextColored(WorkbenchWarningColor, "The base changed after this overlay was saved.");
                ImGui.SameLine();
                if (ImGui.SmallButton("Accept rebase"))
                    replacement = RenderPipelineOverlayEdits.AcceptRebase(pipeline, o);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Stores the current base hash and the renamed anchor ids. Save the script to keep the change.");
            }

            DrawWorkbenchPatchTable(o, overlays, ref replacement);
            ImGui.PopID();
        }

        // The overlay list changes once, after the loop, because a new list rebuilds the command chain.
        if (replacement is not null)
            pipeline.Overlays = replacement;
    }

    private static void DrawWorkbenchPatchTable(int overlayIndex, IReadOnlyList<RenderPipelineOverlay> overlays, ref IReadOnlyList<RenderPipelineOverlay>? replacement)
    {
        if (!ImGui.BeginTable("##WorkbenchPatches", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Patch", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableHeadersRow();
        int shown = 0;
        for (int i = 0; i < _workbenchPatchRows.Count; i++)
        {
            WorkbenchPatchRow row = _workbenchPatchRows[i];
            if (row.OverlayIndex != overlayIndex)
                continue;

            shown++;
            ImGui.PushID(i);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Text);
            ImGui.TableNextColumn();
            ImGui.TextColored(GetWorkbenchPatchStatusColor(row.StatusKind), row.Status);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(row.Message);
            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Revert"))
                replacement = RenderPipelineOverlayEdits.RemovePatch(overlays, overlayIndex, row.Patch) ?? replacement;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Removes the patch from the overlay. Save the script to keep the change.");
            ImGui.PopID();
        }

        ImGui.EndTable();
        if (shown == 0)
            ImGui.TextDisabled("This overlay changes settings only.");
    }

    private static Vector4 GetWorkbenchPatchStatusColor(ERenderPipelinePatchStatus status)
        => status switch
        {
            ERenderPipelinePatchStatus.Applied => WorkbenchOkColor,
            ERenderPipelinePatchStatus.AnchorMoved => WorkbenchWarningColor,
            ERenderPipelinePatchStatus.NotApplied => WorkbenchBadgeColor,
            _ => WorkbenchErrorColor,
        };

    /// <summary>Rebuilds the cached patch rows when the overlay list or the report changes.</summary>
    private static void UpdateWorkbenchPatchRows(RenderPipeline pipeline)
    {
        RenderPipelineOverlayReport report = pipeline.OverlayReport;
        if (ReferenceEquals(_workbenchPatchRowsOverlays, pipeline.Overlays) && ReferenceEquals(_workbenchPatchRowsReport, report))
            return;

        bool overlaysChanged = !ReferenceEquals(_workbenchPatchRowsOverlays, pipeline.Overlays);
        _workbenchPatchRowsOverlays = pipeline.Overlays;
        _workbenchPatchRowsReport = report;
        _workbenchPatchRows.Clear();
        AddWorkbenchPatchRows(report.CommandResults);
        AddWorkbenchPatchRows(report.ResourceResults);
        AddWorkbenchPatchRows(report.SchemaResults);

        // The base hash check builds the base chain, so it runs only when the overlay list changes.
        if (!overlaysChanged)
            return;

        _workbenchOverlayNeedsRebase.Clear();
        for (int o = 0; o < pipeline.Overlays.Count; o++)
            _workbenchOverlayNeedsRebase.Add(RenderPipelineOverlayEdits.NeedsRebase(pipeline, o));
    }

    private static void AddWorkbenchPatchRows(IReadOnlyList<RenderPipelinePatchResult> results)
    {
        for (int i = 0; i < results.Count; i++)
        {
            RenderPipelinePatchResult result = results[i];
            string status = result.Status switch
            {
                ERenderPipelinePatchStatus.Applied => "Applied",
                ERenderPipelinePatchStatus.AnchorMoved => "Anchor renamed",
                ERenderPipelinePatchStatus.AnchorMissing => "Anchor missing",
                ERenderPipelinePatchStatus.Failed => "Failed",
                _ => "Not applied",
            };
            _workbenchPatchRows.Add(new WorkbenchPatchRow(result.OverlayIndex, result.Patch, result.PatchText, status, result.Status, result.Message));
        }
    }
}
