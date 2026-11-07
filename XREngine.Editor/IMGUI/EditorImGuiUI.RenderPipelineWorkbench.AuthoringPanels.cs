using System.Numerics;
using ImGuiNET;
using XREngine.Editor.ComponentEditors;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const string WorkbenchForkReportPopup = "Fork report##WorkbenchForkReport";
    private static readonly string[] WorkbenchSorterNames = ["Submission order", "Near to far", "Far to near", "Opaque state buckets"];
    private static readonly EAuthoredRenderResourceKind[] WorkbenchResourceKinds = Enum.GetValues<EAuthoredRenderResourceKind>();
    private static readonly HashSet<object> _workbenchDraftVisited = new(ReferenceEqualityComparer.Instance);
    private static InspectorTargetSet? _workbenchDraftResourceTargets;
    private static object? _workbenchDraftResourceTarget;
    private static InspectorTargetSet? _workbenchDraftStageTargets;
    private static object? _workbenchDraftStageTarget;
    private static IReadOnlyList<string> _workbenchForkReport = [];
    private static bool _workbenchForkReportRequested;
    private static string? _workbenchSelectedDraftStage;
    private static PipelinePostProcessState? _workbenchStagePreviewState;
    private static RenderPipelinePostProcessSchema? _workbenchStagePreviewSchema;

    /// <summary>
    /// Draws the Fork and Derive actions. Fork copies any pipeline into a new editable pipeline and opens a draft of it.
    /// </summary>
    private static void DrawWorkbenchDeriveAndForkButtons(RenderPipeline pipeline)
    {
        if (ImGui.Button("Fork"))
        {
            XRRenderPipelineInstance? instance = _workbench.Model.Instance;
            RenderPipelineForkResult fork = RenderPipelineFork.Create(pipeline, GetWorkbenchProfile(instance));
            _workbench.Pin(fork.Pipeline, null);
            _workbench.Authoring.BeginEdit(fork.Pipeline);
            _workbenchForkReport = fork.Unconverted;
            _workbenchForkReportRequested = fork.Unconverted.Count > 0;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy this pipeline into a new editable pipeline. Code conditions and factory-only resources become problems to fix.");

        DrawWorkbenchDeriveButton(pipeline);

        if (_workbenchForkReportRequested)
        {
            _workbenchForkReportRequested = false;
            ImGui.OpenPopup(WorkbenchForkReportPopup);
        }

        ImGui.SetNextWindowSize(new Vector2(560f, 360f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup(WorkbenchForkReportPopup))
            return;

        ImGui.TextWrapped("The fork could not convert these items. The Validation view lists the problems that block Apply.");
        ImGui.BeginChild("##ForkReportRows", new Vector2(0f, -ImGui.GetFrameHeightWithSpacing()), ImGuiChildFlags.Borders);
        for (int i = 0; i < _workbenchForkReport.Count; i++)
            ImGui.BulletText(_workbenchForkReport[i]);
        ImGui.EndChild();
        if (ImGui.Button("Close"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /// <summary>
    /// Draws the pass editor of the Inspector for a draft: the sorter and index of a scene pass, and the explicit
    /// dependencies with the redundant edge warning.
    /// </summary>
    private static void DrawWorkbenchPassAuthoring(RenderPassMetadata pass)
    {
        if (_workbench.Authoring.Draft is not { } draft)
            return;

        RenderPipelineDraftState state = draft.State;
        int passIndex = pass.PassIndex;
        ImGui.SeparatorText("Pass authoring");

        if (state.RenderPasses.TryGetValue(passIndex, out var sorter))
        {
            int sorterIndex = GetWorkbenchSorterIndex(sorter);
            ImGui.SetNextItemWidth(200f);
            if (ImGui.Combo("Sorter", ref sorterIndex, WorkbenchSorterNames, WorkbenchSorterNames.Length))
            {
                int chosen = sorterIndex;
                EditWorkbenchDraft("Set pass sorter", s => RenderPipelineDraftPassEdits.SetSorter(s, passIndex, CreateWorkbenchSorter(chosen)));
            }

            int newIndex = passIndex;
            ImGui.SetNextItemWidth(120f);
            if (ImGui.InputInt("Pass index", ref newIndex, 1, 10, ImGuiInputTextFlags.EnterReturnsTrue) && newIndex != passIndex)
                EditWorkbenchDraft("Change pass index", s => RenderPipelineDraftPassEdits.ChangeIndex(s, passIndex, newIndex));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Commands that render this pass keep their old index. Update them in the Inspector.");

            if (ImGui.SmallButton("Remove scene pass"))
                EditWorkbenchDraft("Remove scene pass", s => RenderPipelineDraftPassEdits.RemoveScenePass(s, passIndex));
        }
        else
        {
            ImGui.TextDisabled("This pass is not a scene pass.");
            if (ImGui.SmallButton("Register as scene pass"))
                EditWorkbenchDraft("Register scene pass", s => s.RenderPasses.TryAdd(passIndex, null));
        }

        ImGui.SeparatorText("Depends on");
        for (int i = 0; i < state.PassDependencies.Count; i++)
        {
            AuthoredPassDependency dependency = state.PassDependencies[i];
            if (dependency.PassIndex != passIndex)
                continue;

            ImGui.PushID(i);
            int target = dependency.DependsOnPassIndex;
            if (ImGui.SmallButton("x"))
                EditWorkbenchDraft("Remove pass dependency", s => RenderPipelineDraftPassEdits.RemoveDependency(s, passIndex, target));
            ImGui.SameLine();
            ImGui.TextUnformatted(FindWorkbenchPassTitle(target));
            if (RenderPipelineDraftPassEdits.IsCoveredByResourceEdge(_workbench.Model.Passes, passIndex, target))
            {
                ImGui.SameLine();
                ImGui.TextColored(WorkbenchWarningColor, "(a resource edge already orders these passes)");
            }

            ImGui.PopID();
        }

        ImGui.SetNextItemWidth(220f);
        if (!ImGui.BeginCombo("##AddDependency", "Add dependency..."))
            return;

        foreach (RenderPassMetadata other in _workbench.Model.Passes)
        {
            if (other.PassIndex == passIndex)
                continue;

            ImGui.PushID(other.PassIndex);
            int target = other.PassIndex;
            if (ImGui.Selectable(other.Name))
                EditWorkbenchDraft("Add pass dependency", s => RenderPipelineDraftPassEdits.AddDependency(s, passIndex, target));
            if (ImGui.IsItemHovered() && RenderPipelineDraftPassEdits.IsCoveredByResourceEdge(_workbench.Model.Passes, passIndex, target))
                ImGui.SetTooltip("A resource edge already orders these passes. The explicit edge adds nothing.");
            ImGui.PopID();
        }

        ImGui.EndCombo();
    }

    private static string FindWorkbenchPassTitle(int passIndex)
    {
        foreach (RenderPassMetadata pass in _workbench.Model.Passes)
        {
            if (pass.PassIndex == passIndex)
                return pass.Name;
        }

        return "(missing pass)";
    }

    /// <summary>
    /// Draws the resource authoring actions of a draft: add, duplicate, rename with reference update, delete while
    /// nothing references the resource, and the declaration editor of the selected resource.
    /// </summary>
    private static void DrawWorkbenchResourceAuthoring()
    {
        if (_workbench.Authoring.Draft is not { } draft)
            return;

        RenderPipelineWorkbenchAuthoring authoring = _workbench.Authoring;
        if (ImGui.BeginCombo("##AddResource", "Add resource...", ImGuiComboFlags.WidthFitPreview))
        {
            for (int i = 0; i < WorkbenchResourceKinds.Length; i++)
            {
                EAuthoredRenderResourceKind kind = WorkbenchResourceKinds[i];
                if (!ImGui.Selectable(kind.ToString()))
                    continue;

                AuthoredRenderResource? added = null;
                if (draft.Edit("Add resource", s => added = RenderPipelineDraftResourceEdits.Add(s, kind)) && added is not null)
                    _workbench.Selection.Select(RenderPipelineWorkbenchSelectionEntry.ForResource(added.Name));
            }

            ImGui.EndCombo();
        }

        string? selectedName = _workbench.Selection.ResourceName;
        AuthoredRenderResource? selected = selectedName is null ? null : RenderPipelineDraftResourceEdits.FindResource(draft.State, selectedName);
        if (selected is null)
        {
            if (selectedName is not null)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("The selected resource is not a draft declaration.");
            }

            return;
        }

        string name = selected.Name;
        ImGui.SameLine();
        if (ImGui.Button("Duplicate"))
        {
            AuthoredRenderResource? copy = null;
            if (draft.Edit("Duplicate resource", s => copy = RenderPipelineDraftResourceEdits.Duplicate(s, name)) && copy is not null)
                _workbench.Selection.Select(RenderPipelineWorkbenchSelectionEntry.ForResource(copy.Name));
        }

        ImGui.SameLine();
        if (ImGui.Button("Delete"))
        {
            List<string> blocked = authoring.BlockedDeleteReferences;
            if (draft.TryEdit("Delete resource", s => RenderPipelineDraftResourceEdits.TryDelete(s, name, blocked)))
                _workbench.Selection.Clear();
        }

        if (authoring.BlockedDeleteReferences.Count > 0)
        {
            ImGui.TextColored(WorkbenchWarningColor, "Delete is blocked. These items reference the resource:");
            for (int i = 0; i < authoring.BlockedDeleteReferences.Count; i++)
                ImGui.BulletText(authoring.BlockedDeleteReferences[i]);
        }

        if (!string.Equals(authoring.RenameTarget, name, StringComparison.Ordinal))
        {
            authoring.RenameTarget = name;
            authoring.RenameBuffer = name;
            authoring.BlockedDeleteReferences.Clear();
        }

        string rename = authoring.RenameBuffer;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.InputText("##RenameResource", ref rename, 128))
            authoring.RenameBuffer = rename;
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(rename) || string.Equals(rename, name, StringComparison.Ordinal));
        if (ImGui.Button("Rename"))
        {
            string newName = rename.Trim();
            if (draft.TryEdit("Rename resource", s => RenderPipelineDraftResourceEdits.Rename(s, name, newName) >= 0))
            {
                _workbench.Selection.Select(RenderPipelineWorkbenchSelectionEntry.ForResource(newName));
            }
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Renames the declaration and every command property, attachment, view source, and quad input that names it.");

        if (!ImGui.CollapsingHeader("Declaration", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (!ReferenceEquals(_workbenchDraftResourceTarget, selected))
        {
            _workbenchDraftResourceTarget = selected;
            _workbenchDraftResourceTargets = new InspectorTargetSet([selected], selected.GetType());
        }

        _workbenchDraftVisited.Clear();
        DrawWorkbenchDraftObject(_workbenchDraftResourceTargets!, "WorkbenchDraftResource", _workbenchDraftVisited, "Edit resource declaration");
    }

    /// <summary>
    /// Draws the stage authoring section of a draft: add a library or custom stage, remove a stage, edit the selected
    /// declaration, and preview the stage with the camera editor drawer against a scratch state.
    /// </summary>
    private static void DrawWorkbenchStageAuthoring()
    {
        if (_workbench.Authoring.Draft is not { } draft)
            return;
        if (!ImGui.CollapsingHeader("Draft stages", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        RenderPipelineDraftState state = draft.State;
        if (ImGui.Button("Add custom stage"))
        {
            string key = MakeWorkbenchStageKey(state, "customStage");
            if (draft.Edit("Add stage", s => s.Stages.Add(new AuthoredPostProcessStage { Key = key, DisplayName = key, Order = s.Stages.Count })))
                _workbenchSelectedDraftStage = key;
        }

        ImGui.SameLine();
        if (ImGui.BeginCombo("##AddLibraryStage", "Add library stage...", ImGuiComboFlags.WidthFitPreview))
        {
            IReadOnlyList<string> keys = AuthoredPostProcessSchema.LibraryStageKeys;
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                bool present = FindWorkbenchDraftStage(state, key) is not null;
                if (!ImGui.Selectable(key, false, present ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None))
                    continue;

                if (draft.Edit("Add library stage", s => s.Stages.Add(new AuthoredPostProcessStage { Key = key, DisplayName = key, IsLibraryStage = true, Order = s.Stages.Count })))
                    _workbenchSelectedDraftStage = key;
            }

            ImGui.EndCombo();
        }

        for (int i = 0; i < state.Stages.Count; i++)
        {
            AuthoredPostProcessStage stage = state.Stages[i];
            ImGui.PushID(i);
            string key = stage.Key;
            if (ImGui.SmallButton("x"))
            {
                EditWorkbenchDraft("Remove stage", s => s.Stages.RemoveAll(x => string.Equals(x.Key, key, StringComparison.Ordinal)) > 0);
                ImGui.PopID();
                break;
            }

            ImGui.SameLine();
            if (ImGui.Selectable(key, string.Equals(_workbenchSelectedDraftStage, key, StringComparison.Ordinal)))
                _workbenchSelectedDraftStage = key;
            if (stage.IsLibraryStage)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(library)");
            }
            ImGui.PopID();
        }

        AuthoredPostProcessStage? selected = _workbenchSelectedDraftStage is null ? null : FindWorkbenchDraftStage(state, _workbenchSelectedDraftStage);
        if (selected is null)
            return;

        ImGui.SeparatorText(selected.Key);
        if (!ReferenceEquals(_workbenchDraftStageTarget, selected))
        {
            _workbenchDraftStageTarget = selected;
            _workbenchDraftStageTargets = new InspectorTargetSet([selected], selected.GetType());
        }

        _workbenchDraftVisited.Clear();
        DrawWorkbenchDraftObject(_workbenchDraftStageTargets!, "WorkbenchDraftStage", _workbenchDraftVisited, "Edit stage declaration");
        DrawWorkbenchStagePreview(selected.Key);
    }

    /// <summary>
    /// Draws the camera editor controls of a draft stage against a scratch state. The scratch state follows the schema
    /// of the draft preview, so it rebuilds after each stage edit.
    /// </summary>
    private static void DrawWorkbenchStagePreview(string stageKey)
    {
        if (_workbench.Authoring.Draft is null)
            return;

        RenderPipelinePostProcessSchema schema = _workbench.Authoring.GetPreview(GetWorkbenchProfile(_workbench.Model.Instance)).Schema;
        if (!schema.TryGetStage(stageKey, out PostProcessStageDescriptor? descriptor) || descriptor is null)
        {
            ImGui.TextColored(WorkbenchErrorColor, "The stage is not valid. The Validation view shows why.");
            return;
        }

        if (!ReferenceEquals(_workbenchStagePreviewSchema, schema))
        {
            _workbenchStagePreviewSchema = schema;
            _workbenchStagePreviewState = PipelinePostProcessState.CreatePreview(schema);
        }

        if (_workbenchStagePreviewState?.GetStage(stageKey) is not { } stageState)
            return;

        ImGui.SeparatorText("Camera preview");
        ImGui.TextDisabled("A camera shows these controls for the stage. Values here are scratch values.");
        PostProcessSchemaStageDrawer.DrawStage(descriptor, stageState, null, null, descriptor.CustomDrawer, drawStageHeader: false);
    }

    private static AuthoredPostProcessStage? FindWorkbenchDraftStage(RenderPipelineDraftState state, string key)
    {
        for (int i = 0; i < state.Stages.Count; i++)
        {
            if (string.Equals(state.Stages[i].Key, key, StringComparison.Ordinal))
                return state.Stages[i];
        }

        return null;
    }

    private static string MakeWorkbenchStageKey(RenderPipelineDraftState state, string baseKey)
    {
        if (FindWorkbenchDraftStage(state, baseKey) is null)
            return baseKey;

        for (int suffix = 2; ; suffix++)
        {
            string key = string.Concat(baseKey, suffix.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (FindWorkbenchDraftStage(state, key) is null)
                return key;
        }
    }
}
