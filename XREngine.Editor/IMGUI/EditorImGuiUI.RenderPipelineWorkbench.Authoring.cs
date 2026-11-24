using System.Numerics;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Pipelines.Validation;
using XREngine.Rendering.Resources;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const string WorkbenchCommandPayload = "XRE_WB_COMMAND";
    private const string WorkbenchPalettePayload = "XRE_WB_PALETTE";
    private const string WorkbenchPalettePopup = "##WorkbenchPalette";

    private static readonly List<Type> _workbenchPushTypes = [];
    private static readonly List<string> _workbenchPushTypeLabels = [];
    private static RenderPipelineDraftState? _workbenchPropertyEditBefore;
    private static string _workbenchDraftStatus = string.Empty;
    private static long _workbenchDraftStatusKey = long.MinValue;
    private static bool _workbenchPaletteFocusSearch;

    /// <summary>Returns true when the Workbench edits a draft of <paramref name="pipeline"/>.</summary>
    private static bool IsWorkbenchEditing(RenderPipeline? pipeline)
        => _workbench.Authoring.IsEditing(pipeline);

    /// <summary>Returns the resource profile of the selected instance, or the validation default profile.</summary>
    private static RenderPipelineResourceProfile GetWorkbenchProfile(XRRenderPipelineInstance? instance)
        => instance?.ActiveGeneration?.Layout.Profile ?? RenderPipelineValidationContext.DefaultProfile;

    /// <summary>
    /// Refreshes the model from the open draft when one exists, otherwise from the live pipeline. A draft of another
    /// pipeline closes, because the Workbench edits one pipeline at a time.
    /// </summary>
    private static void RefreshWorkbenchModel(RenderPipeline pipeline, XRRenderPipelineInstance? instance)
    {
        RenderPipelineWorkbenchAuthoring authoring = _workbench.Authoring;
        if (authoring.Draft is { } draft && !ReferenceEquals(draft.Pipeline, pipeline))
            authoring.Close();

        if (!authoring.IsEditing(pipeline))
        {
            _workbench.Model.Refresh(pipeline, instance);
            return;
        }

        RenderPipelineResourceProfile profile = GetWorkbenchProfile(instance);
        authoring.Update(instance, profile, AbstractRenderer.Current?.BackendId ?? RendererBackendId.OpenGL);
        _workbench.Model.Refresh(RenderPipelineWorkbenchInputs.FromDraft(authoring.Draft!, authoring.GetPreview(profile), instance));
    }

    /// <summary>
    /// Draws the authoring row under the target bar: the read-only notice of a code pipeline, the Edit button of a
    /// custom pipeline, or the draft controls.
    /// </summary>
    private static void DrawWorkbenchAuthoringBar(RenderPipeline pipeline)
    {
        RenderPipelineWorkbenchAuthoring authoring = _workbench.Authoring;
        if (pipeline is not CustomRenderPipeline custom)
        {
            ImGui.TextColored(WorkbenchInfoColor, "[Locked] Code pipeline");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Code builds this pipeline, so the Workbench cannot edit it. Derive or fork it to make an editable pipeline.");
            ImGui.SameLine();
            DrawWorkbenchDeriveAndForkButtons(pipeline);
            return;
        }

        if (!authoring.IsEditing(pipeline))
        {
            if (ImGui.Button("Edit"))
                authoring.BeginEdit(custom);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Open a draft. Edits change the draft until you apply it.");
            ImGui.SameLine();
            DrawWorkbenchDeriveAndForkButtons(pipeline);
            return;
        }

        RenderPipelineDraft draft = authoring.Draft!;
        bool valid = authoring.CanApply(out RenderPipelineValidationReport? report);
        bool canApply = valid && draft.IsDirty && !draft.IsApplyPending;
        ImGui.BeginDisabled(!canApply);
        if (ImGui.Button("Apply"))
            draft.Apply();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(canApply ? "Write the draft to the pipeline on the render thread."
                : report is null ? "The draft is validating."
                : !valid ? "Fix the errors in the Validation view first."
                : draft.IsApplyPending ? "An Apply is in progress."
                : "The draft has no changes to apply.");
        }

        ImGui.SameLine();
        bool autoApply = draft.AutoApply;
        if (ImGui.Checkbox("Auto-apply", ref autoApply))
            draft.AutoApply = autoApply;

        ImGui.SameLine();
        if (ImGui.Button("Insert..."))
            OpenWorkbenchPalette();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open the command palette (Ctrl+Space).");

        ImGui.SameLine();
        if (ImGui.Button("Revert"))
            draft.Revert();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Replace the draft with the pipeline data. This step has no undo.");

        ImGui.SameLine();
        if (ImGui.Button("Close draft"))
        {
            authoring.Close();
            return;
        }

        ImGui.SameLine();
        UpdateWorkbenchDraftStatus(draft);
        if (draft.ApplyFailure is not null)
            ImGui.TextColored(WorkbenchErrorColor, _workbenchDraftStatus);
        else
            ImGui.TextColored(draft.IsDirty ? WorkbenchWarningColor : WorkbenchOkColor, _workbenchDraftStatus);
    }

    /// <summary>Builds the draft status text only when the revision, the apply state, or the failure changed.</summary>
    private static void UpdateWorkbenchDraftStatus(RenderPipelineDraft draft)
    {
        long key = ((long)draft.Revision << 32) ^ ((long)draft.AppliedRevision << 1) ^ (draft.IsApplyPending ? 1L : 0L) ^ (draft.ApplyFailure?.GetHashCode() ?? 0);
        if (key == _workbenchDraftStatusKey)
            return;

        _workbenchDraftStatusKey = key;
        _workbenchDraftStatus = draft.ApplyFailure is { } failure ? string.Concat("Apply failed: ", failure, " The draft is kept.")
            : draft.IsApplyPending ? "Applying..."
            : draft.IsDirty ? string.Concat("Draft revision ", RenderPipelineWorkbenchText.FormatCount(draft.Revision), " is not applied")
            : "Draft matches the pipeline";
    }

    /// <summary>Opens the command palette in the next frame.</summary>
    private static void OpenWorkbenchPalette()
    {
        _workbench.Authoring.Palette.OpenRequested = true;
        _workbenchPaletteFocusSearch = true;
    }

    /// <summary>
    /// Handles the authoring shortcuts while the Workbench window has focus and no text field takes input:
    /// Delete, Ctrl+D, Ctrl+Space, and Alt+Up or Alt+Down.
    /// </summary>
    private static void HandleWorkbenchAuthoringShortcuts(RenderPipeline pipeline)
    {
        if (!IsWorkbenchEditing(pipeline) || !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || ImGui.GetIO().WantTextInput)
            return;

        ImGuiIOPtr io = ImGui.GetIO();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Space))
        {
            OpenWorkbenchPalette();
            return;
        }

        ViewportRenderCommand? command = GetWorkbenchSelectedCommand();
        if (command is null)
            return;

        if (ImGui.IsKeyPressed(ImGuiKey.Delete))
            EditWorkbenchDraft("Delete command", state => RenderPipelineDraftEdits.Delete(state, command));
        else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D))
            EditWorkbenchDraftAndSelect("Duplicate command", state => RenderPipelineDraftEdits.Duplicate(state, command));
        else if (io.KeyAlt && ImGui.IsKeyPressed(ImGuiKey.UpArrow))
            EditWorkbenchDraft("Move command", state => RenderPipelineDraftEdits.MoveBy(state, command, -1));
        else if (io.KeyAlt && ImGui.IsKeyPressed(ImGuiKey.DownArrow))
            EditWorkbenchDraft("Move command", state => RenderPipelineDraftEdits.MoveBy(state, command, 1));
    }

    /// <summary>Draws the Edit menu of the menu bar. Each item acts on the selected command of the draft.</summary>
    private static void DrawWorkbenchEditMenu(RenderPipeline? pipeline)
    {
        bool editing = IsWorkbenchEditing(pipeline);
        if (!ImGui.BeginMenu("Edit", editing))
            return;

        if (ImGui.MenuItem("Insert...", "Ctrl+Space"))
            OpenWorkbenchPalette();

        ViewportRenderCommand? command = GetWorkbenchSelectedCommand();
        if (command is null)
        {
            ImGui.TextDisabled("Select a command to edit it.");
        }
        else
        {
            ImGui.Separator();
            DrawWorkbenchCommandEditItems(command);
        }

        ImGui.EndMenu();
    }

    /// <summary>Draws the structural edit items for <paramref name="command"/>. The Edit menu and context menus share them.</summary>
    private static void DrawWorkbenchCommandEditItems(ViewportRenderCommand command)
    {
        if (ImGui.MenuItem("Duplicate", "Ctrl+D"))
            EditWorkbenchDraftAndSelect("Duplicate command", state => RenderPipelineDraftEdits.Duplicate(state, command));
        if (ImGui.MenuItem("Delete", "Del"))
            EditWorkbenchDraft("Delete command", state => RenderPipelineDraftEdits.Delete(state, command));
        if (ImGui.MenuItem("Move earlier", "Alt+Up"))
            EditWorkbenchDraft("Move command", state => RenderPipelineDraftEdits.MoveBy(state, command, -1));
        if (ImGui.MenuItem("Move later", "Alt+Down"))
            EditWorkbenchDraft("Move command", state => RenderPipelineDraftEdits.MoveBy(state, command, 1));

        ImGui.Separator();
        if (ImGui.MenuItem("Wrap in If"))
            EditWorkbenchDraftAndSelect("Wrap in If", state => RenderPipelineDraftEdits.WrapInIf(state, command));
        if (ImGui.BeginMenu("Wrap in Scope"))
        {
            EnsureWorkbenchPushTypes();
            for (int i = 0; i < _workbenchPushTypes.Count; i++)
            {
                Type pushType = _workbenchPushTypes[i];
                if (ImGui.MenuItem(_workbenchPushTypeLabels[i]))
                    EditWorkbenchDraftAndSelect("Wrap in Scope", state => RenderPipelineDraftEdits.WrapInScope(state, command, pushType));
            }

            ImGui.EndMenu();
        }

        if (ImGui.MenuItem("Extract to Branch"))
            EditWorkbenchDraftAndSelect("Extract to Branch", state => RenderPipelineDraftEdits.ExtractToBranch(state, command));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Move the command into the true branch of a new If, with a copy in the false branch.");
    }

    /// <summary>Builds the list of push state command types for "Wrap in Scope" once.</summary>
    private static void EnsureWorkbenchPushTypes()
    {
        if (_workbenchPushTypes.Count > 0)
            return;

        IReadOnlyList<RenderCommandCatalogEntry> entries = RenderCommandCatalog.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            RenderCommandCatalogEntry entry = entries[i];
            if (!typeof(ViewportStateRenderCommandBase).IsAssignableFrom(entry.Type) || entry.Info.Category == ERenderCommandCategory.Flow || entry.Info.RequiresCode)
                continue;

            _workbenchPushTypes.Add(entry.Type);
            _workbenchPushTypeLabels.Add(entry.Info.DisplayName);
        }
    }

    /// <summary>Returns the selected command when the model shows the draft, or null.</summary>
    private static ViewportRenderCommand? GetWorkbenchSelectedCommand()
    {
        int nodeId = _workbench.Selection.NodeId;
        if (nodeId == 0 || !_workbench.Model.TryGetOutlineRow(nodeId, out RenderPipelineOutlineRow row))
            return null;

        return row.Command;
    }

    /// <summary>Runs one edit on the open draft and records undo.</summary>
    private static void EditWorkbenchDraft(string description, Func<RenderPipelineDraftState, bool> edit)
    {
        if (_workbench.Authoring.Draft is not { } draft)
            return;

        draft.TryEdit(description, edit);
    }

    /// <summary>Runs one edit that returns a new command, records undo, and selects the new command.</summary>
    private static void EditWorkbenchDraftAndSelect(string description, Func<RenderPipelineDraftState, ViewportRenderCommand?> edit)
    {
        if (_workbench.Authoring.Draft is not { } draft)
            return;

        ViewportRenderCommand? created = null;
        bool applied = draft.TryEdit(description, state => (created = edit(state)) is not null);

        if (applied && created is not null)
            _workbench.Selection.Select(RenderPipelineWorkbenchSelectionEntry.ForCommand(RenderPipelineWorkbenchNodeIds.Get(created)));
    }

    /// <summary>
    /// Adds drag and drop and the context menu to the last item, which shows <paramref name="command"/>. A command
    /// dropped on it moves before it. A palette row dropped on it inserts after it.
    /// </summary>
    private static unsafe void DrawWorkbenchCommandDragDrop(ViewportRenderCommand command, string label)
    {
        if (!IsWorkbenchEditing(_workbench.Model.Pipeline))
            return;

        if (ImGui.BeginDragDropSource())
        {
            int nodeId = RenderPipelineWorkbenchNodeIds.Get(command);
            ImGui.SetDragDropPayload(WorkbenchCommandPayload, (IntPtr)(&nodeId), sizeof(int));
            ImGui.TextUnformatted(label);
            ImGui.EndDragDropSource();
        }

        if (ImGui.BeginDragDropTarget())
        {
            ImGuiPayloadPtr moved = ImGui.AcceptDragDropPayload(WorkbenchCommandPayload);
            if (moved.NativePtr != null && moved.Data != IntPtr.Zero)
            {
                int movedId = *(int*)moved.Data;
                if (_workbench.Model.TryGetOutlineRow(movedId, out RenderPipelineOutlineRow movedRow) && movedRow.Command is { } movedCommand)
                    EditWorkbenchDraft("Move command", state => RenderPipelineDraftEdits.MoveBefore(state, movedCommand, command));
            }

            ImGuiPayloadPtr inserted = ImGui.AcceptDragDropPayload(WorkbenchPalettePayload);
            if (inserted.NativePtr != null && inserted.Data != IntPtr.Zero)
            {
                int itemIndex = *(int*)inserted.Data;
                IReadOnlyList<RenderPipelinePaletteItem> items = _workbench.Authoring.Palette.Items;
                if ((uint)itemIndex < (uint)items.Count)
                    InsertWorkbenchPaletteItem(items[itemIndex], command);
            }

            ImGui.EndDragDropTarget();
        }

        if (ImGui.BeginPopupContextItem("##CommandEdit"))
        {
            DrawWorkbenchCommandEditItems(command);
            ImGui.EndPopup();
        }
    }

    /// <summary>Draws the command palette popup. Enter inserts the highlighted row after the selection.</summary>
    private static unsafe void DrawWorkbenchPalette(RenderPipeline pipeline)
    {
        RenderPipelineCommandPalette palette = _workbench.Authoring.Palette;
        if (palette.OpenRequested)
        {
            palette.OpenRequested = false;
            ImGui.OpenPopup(WorkbenchPalettePopup);
        }

        ImGui.SetNextWindowSize(new Vector2(520f, 440f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup(WorkbenchPalettePopup))
        {
            palette.IsOpen = false;
            return;
        }

        palette.IsOpen = true;
        palette.Refresh(hideCodeCommands: pipeline is CustomRenderPipeline);
        if (_workbenchPaletteFocusSearch)
        {
            ImGui.SetKeyboardFocusHere();
            _workbenchPaletteFocusSearch = false;
        }

        string search = palette.Search;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##PaletteSearch", "Search commands and snippets", ref search, 128))
        {
            palette.Search = search;
            palette.Highlighted = 0;
            palette.Refresh(hideCodeCommands: pipeline is CustomRenderPipeline);
        }

        IReadOnlyList<RenderPipelinePaletteItem> items = palette.Items;
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
            palette.Highlighted = Math.Min(items.Count - 1, palette.Highlighted + 1);
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
            palette.Highlighted = Math.Max(0, palette.Highlighted - 1);
        if ((ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)) && (uint)palette.Highlighted < (uint)items.Count)
        {
            InsertWorkbenchPaletteItem(items[palette.Highlighted], GetWorkbenchSelectedCommand());
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        float detailHeight = ImGui.GetTextLineHeightWithSpacing() * 3f;
        ImGui.BeginChild("##PaletteRows", new Vector2(0f, -detailHeight), ImGuiChildFlags.Borders);
        ImGuiListClipper clipper = new();
        ImGuiNative.ImGuiListClipper_Begin(&clipper, items.Count, -1f);
        try
        {
            while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    RenderPipelinePaletteItem item = items[i];
                    ImGui.PushID(i);
                    if (ImGui.Selectable(item.Label, i == palette.Highlighted, ImGuiSelectableFlags.AllowDoubleClick))
                    {
                        palette.Highlighted = i;
                        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        {
                            InsertWorkbenchPaletteItem(item, GetWorkbenchSelectedCommand());
                            ImGui.CloseCurrentPopup();
                        }
                    }

                    if (ImGui.BeginDragDropSource())
                    {
                        int index = i;
                        ImGui.SetDragDropPayload(WorkbenchPalettePayload, (IntPtr)(&index), sizeof(int));
                        ImGui.TextUnformatted(item.Label);
                        ImGui.EndDragDropSource();
                    }

                    ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - 120f));
                    ImGui.TextDisabled(item.Category);
                    ImGui.PopID();
                }
            }
        }
        finally
        {
            ImGuiNative.ImGuiListClipper_End(&clipper);
        }

        ImGui.EndChild();
        if ((uint)palette.Highlighted < (uint)items.Count)
        {
            RenderPipelinePaletteItem highlighted = items[palette.Highlighted];
            ImGui.TextWrapped(highlighted.Description.Length > 0 ? highlighted.Description : highlighted.Label);
            ImGui.TextDisabled(highlighted.Detail);
        }

        ImGui.EndPopup();
    }

    /// <summary>
    /// Inserts a palette row after <paramref name="anchor"/>, or at the end of the selected container, or at the end of
    /// the root container. The first inserted command becomes the selection.
    /// </summary>
    private static void InsertWorkbenchPaletteItem(RenderPipelinePaletteItem item, ViewportRenderCommand? anchor)
    {
        ViewportRenderCommandContainer? container = null;
        if (anchor is null &&
            _workbench.Selection.Current.Kind == ERenderPipelineWorkbenchSelectionKind.Container &&
            _workbench.Model.TryGetOutlineRow(_workbench.Selection.NodeId, out RenderPipelineOutlineRow row))
        {
            container = row.Container;
        }

        EditWorkbenchDraftAndSelect(string.Concat("Insert ", item.Label), state =>
        {
            List<ViewportRenderCommand> commands = item.Snippet is { } snippet
                ? snippet.Build(state)
                : [RenderCommandGraphCloner.CreateCommand(item.Command!.Type)];
            bool inserted = container is not null
                ? RenderPipelineDraftEdits.AppendTo(state, container, commands)
                : RenderPipelineDraftEdits.InsertAfter(state, anchor, commands);
            return inserted && commands.Count > 0 ? commands[0] : null;
        });
    }

    /// <summary>
    /// Draws an inspectable object of the draft and turns each edit gesture into one draft undo step. The method
    /// takes a copy of the draft when a gesture starts, marks the draft changed on each edit, and records the step when
    /// the gesture ends.
    /// </summary>
    private static void DrawWorkbenchDraftObject(InspectorTargetSet targets, string id, HashSet<object> visited, string description)
    {
        RenderPipelineDraft? draft = _workbench.Authoring.Draft;
        ImGui.BeginGroup();
        DrawInspectableObject(targets, id, visited);
        ImGui.EndGroup();
        if (draft is null)
            return;

        if (ImGui.IsItemActivated())
            _workbenchPropertyEditBefore = draft.State.Clone();
        if (ImGui.IsItemEdited())
        {
            _workbenchPropertyEditBefore ??= draft.State.Clone();
            draft.MarkEdited();
        }

        if (ImGui.IsItemDeactivatedAfterEdit() && _workbenchPropertyEditBefore is { } before)
        {
            draft.RecordEdit(description, before);
            _workbenchPropertyEditBefore = null;
        }
        else if (ImGui.IsItemDeactivated())
        {
            _workbenchPropertyEditBefore = null;
        }
    }

    /// <summary>Returns the name of a sorter for the pass editor.</summary>
    private static int GetWorkbenchSorterIndex(IComparer<RenderCommand>? sorter)
        => sorter switch
        {
            NearToFarRenderCommandSorter => 1,
            FarToNearRenderCommandSorter => 2,
            OpaqueStateBucketRenderCommandSorter => 3,
            _ => 0,
        };

    private static IComparer<RenderCommand>? CreateWorkbenchSorter(int index)
        => index switch
        {
            1 => new NearToFarRenderCommandSorter(),
            2 => new FarToNearRenderCommandSorter(),
            3 => new OpaqueStateBucketRenderCommandSorter(),
            _ => null,
        };
}
