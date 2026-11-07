using System.Numerics;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static readonly Vector4 WorkbenchBadgeColor = new(0.55f, 0.75f, 0.95f, 1f);
    private static readonly Vector4 WorkbenchWarningColor = new(0.95f, 0.65f, 0.3f, 1f);
    private static readonly Vector4 WorkbenchErrorColor = new(0.95f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 WorkbenchOkColor = new(0.5f, 0.85f, 0.5f, 1f);

    private static readonly List<RenderPipeline> _workbenchPipelineCandidates = [];
    private static string _workbenchGenerationBadge = string.Empty;
    private static int _workbenchGenerationBadgeKey = int.MinValue;
    private static string _workbenchBackendBadge = string.Empty;
    private static string _workbenchBackendBadgeKey = "\0";

    private static void DrawWorkbenchTargetBar(RenderPipeline pipeline, XRRenderPipelineInstance? instance)
    {
        DrawWorkbenchHistoryButtons();
        ImGui.SameLine();
        DrawWorkbenchPipelineSelector(pipeline);
        ImGui.SameLine();
        DrawWorkbenchInstanceSelector(pipeline, instance);
        ImGui.SameLine();
        DrawWorkbenchGenerationBadge(instance);
        ImGui.SameLine();
        DrawWorkbenchBackendBadge();
        DrawWorkbenchDerivedBadge(pipeline);
        ImGui.SameLine();
        DrawWorkbenchLiveToggle();
        DrawWorkbenchTargetBarExtras(pipeline, instance);
    }

    private static void DrawWorkbenchHistoryButtons()
    {
        RenderPipelineWorkbenchSelection selection = _workbench.Selection;
        ImGui.BeginDisabled(!selection.CanGoBack);
        if (ImGui.ArrowButton("##WorkbenchBack", ImGuiDir.Left))
            selection.Back();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Back");

        ImGui.SameLine();
        ImGui.BeginDisabled(!selection.CanGoForward);
        if (ImGui.ArrowButton("##WorkbenchForward", ImGuiDir.Right))
            selection.Forward();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Forward");
    }

    private static void DrawWorkbenchPipelineSelector(RenderPipeline pipeline)
    {
        ImGui.SetNextItemWidth(220f);
        if (!ImGui.BeginCombo("##WorkbenchPipeline", pipeline.DebugName))
            return;

        // The candidate list is built only while the combo is open.
        _workbenchPipelineCandidates.Clear();
        foreach (XRViewport? viewport in RuntimeEngine.EnumerateActiveViewports())
        {
            AddWorkbenchPipelineCandidate(viewport?.RenderPipeline);
            AddWorkbenchPipelineCandidate(viewport?.ActiveCamera?.RenderPipeline);
        }

        AddWorkbenchPipelineCandidate(pipeline);
        for (int i = 0; i < _workbenchPipelineCandidates.Count; i++)
        {
            RenderPipeline candidate = _workbenchPipelineCandidates[i];
            ImGui.PushID(i);
            if (ImGui.Selectable(candidate.DebugName, ReferenceEquals(candidate, pipeline)))
                _workbench.Pin(candidate, null);
            ImGui.PopID();
        }

        ImGui.Separator();
        if (ImGui.Selectable("Follow the active pipeline", _workbench.PinnedPipeline is null))
            _workbench.Unpin();

        ImGui.EndCombo();
    }

    private static void AddWorkbenchPipelineCandidate(RenderPipeline? pipeline)
    {
        if (pipeline is null || pipeline.IsDestroyed)
            return;

        for (int i = 0; i < _workbenchPipelineCandidates.Count; i++)
        {
            if (ReferenceEquals(_workbenchPipelineCandidates[i], pipeline))
                return;
        }

        _workbenchPipelineCandidates.Add(pipeline);
    }

    private static void DrawWorkbenchInstanceSelector(RenderPipeline pipeline, XRRenderPipelineInstance? instance)
    {
        IReadOnlyList<XRRenderPipelineInstance> instances = pipeline.Instances;
        ImGui.SetNextItemWidth(360f);
        string preview = instance is null ? "No live instance" : RenderPipelineWorkbenchInstanceLabels.Get(instance);
        if (!ImGui.BeginCombo("##WorkbenchInstance", preview))
            return;

        for (int i = 0; i < instances.Count; i++)
        {
            XRRenderPipelineInstance candidate = instances[i];
            ImGui.PushID(candidate.InstanceId);
            if (ImGui.Selectable(RenderPipelineWorkbenchInstanceLabels.Get(candidate), ReferenceEquals(candidate, instance)))
            {
                if (_workbench.PinnedPipeline is null)
                    _workbench.Pin(pipeline, candidate);
                else
                    _workbench.PinInstance(candidate);
                _workbench.LastInstanceIndex = i;
            }

            ImGui.PopID();
        }

        if (instances.Count == 0)
            ImGui.TextDisabled("The pipeline has no live instance.");

        ImGui.EndCombo();
    }

    private static void DrawWorkbenchGenerationBadge(XRRenderPipelineInstance? instance)
    {
        if (instance is null)
        {
            ImGui.TextDisabled("No generation");
            return;
        }

        RenderResourceGeneration? active = instance.ActiveGeneration;
        RenderResourceGeneration? pending = instance.PendingGeneration;
        int retired = instance.RetiredGenerations.Count;
        int key = HashCode.Combine(active, pending, retired);
        if (key != _workbenchGenerationBadgeKey)
        {
            _workbenchGenerationBadgeKey = key;
            _workbenchGenerationBadge = string.Concat(
                "Gen ",
                active is null ? "none" : "active",
                pending is null ? string.Empty : " + pending",
                retired > 0 ? string.Concat(" | retired ", RenderPipelineWorkbenchText.FormatCount(retired)) : string.Empty);
        }

        Vector4 color = active is null ? WorkbenchWarningColor : pending is null ? WorkbenchOkColor : WorkbenchBadgeColor;
        ImGui.TextColored(color, _workbenchGenerationBadge);
        if (!ImGui.IsItemHovered())
            return;

        // The key text is formatted only while the tooltip shows.
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(active is null ? "No active generation." : active.Key.ToString());
        if (pending is not null)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Pending:");
            ImGui.TextUnformatted(pending.Key.ToString());
        }

        if (instance.LastResourceGenerationFailure is { Length: > 0 } failure)
        {
            ImGui.Separator();
            ImGui.TextColored(WorkbenchErrorColor, failure);
        }

        ImGui.EndTooltip();
    }

    private static void DrawWorkbenchBackendBadge()
    {
        string backend = AbstractRenderer.Current?.BackendId.Value ?? string.Empty;
        if (!string.Equals(backend, _workbenchBackendBadgeKey, StringComparison.Ordinal))
        {
            _workbenchBackendBadgeKey = backend;
            _workbenchBackendBadge = backend.Length == 0 ? "Backend: none" : string.Concat("Backend: ", backend);
        }

        ImGui.TextColored(WorkbenchBadgeColor, _workbenchBackendBadge);
        DrawWorkbenchPlanBadge();
    }

    private static void DrawWorkbenchLiveToggle()
    {
        bool frozen = _workbench.Model.IsFrozen;
        if (ImGui.RadioButton("Live", !frozen))
            _workbench.Model.IsFrozen = false;
        ImGui.SameLine();
        if (ImGui.RadioButton("Frozen", frozen))
            _workbench.Model.IsFrozen = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Frozen keeps the last captured data and timing while the scene changes.");
    }
}
