using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static readonly HashSet<object> _workbenchInspectorVisited = new(ReferenceEqualityComparer.Instance);
    private static InspectorTargetSet? _workbenchInspectorTargets;
    private static object? _workbenchInspectorTargetObject;

    private static void DrawWorkbenchInspector()
    {
        RenderPipelineWorkbenchInspectorContent content = _workbench.Inspector;
        content.Update(_workbench.Model, _workbench.Selection.Current);

        if (_workbench.Selection.Current.Kind == ERenderPipelineWorkbenchSelectionKind.None)
        {
            ImGui.TextDisabled("Select a command, a pass, or a resource in any view.");
            return;
        }

        ImGui.TextUnformatted(content.Title);
        ImGui.Separator();
        DrawWorkbenchItems(content.Items);
        if (content.Pass is { } pass && IsWorkbenchEditing(_workbench.Model.Pipeline))
            DrawWorkbenchPassAuthoring(pass);

        object? editable = (object?)content.Command ?? content.Container;
        if (editable is null)
            return;

        ImGui.Spacing();
        ImGui.SeparatorText("Properties");
        RenderPipeline? pipeline = _workbench.Model.Pipeline;
        bool editing = IsWorkbenchEditing(pipeline);
        if (pipeline is not null && !RenderPipelineWorkbenchAuthoring.CanAuthor(pipeline))
            ImGui.TextDisabled("Code pipelines rebuild their commands from code. Changes here last until the next rebuild.");
        else if (!editing)
            ImGui.TextDisabled("Changes here change the live pipeline. Use Edit to change a draft with undo and validation.");

        if (!ReferenceEquals(_workbenchInspectorTargetObject, editable))
        {
            _workbenchInspectorTargetObject = editable;
            _workbenchInspectorTargets = new InspectorTargetSet([editable], editable.GetType());
        }

        _workbenchInspectorVisited.Clear();
        if (editing)
            DrawWorkbenchDraftObject(_workbenchInspectorTargets!, content.PropertyEditorId, _workbenchInspectorVisited, "Edit command property");
        else
            DrawInspectableObject(_workbenchInspectorTargets!, content.PropertyEditorId, _workbenchInspectorVisited);
    }

    /// <summary>
    /// Draws cached Inspector lines: headers as separators, linked lines as selectables, and other lines as wrapped text.
    /// </summary>
    private static void DrawWorkbenchItems(List<RenderPipelineWorkbenchInspectorItem> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            RenderPipelineWorkbenchInspectorItem item = items[i];
            if (item.IsHeader)
            {
                ImGui.Spacing();
                ImGui.SeparatorText(item.Text);
                continue;
            }

            if (item.Indent > 0)
                ImGui.Indent(item.Indent * 12f);

            if (item.HasLink)
            {
                ImGui.PushID(i);
                if (ImGui.Selectable(item.Text))
                    _workbench.Selection.Select(item.Link);
                ImGui.PopID();
            }
            else
            {
                ImGui.TextWrapped(item.Text);
            }

            if (item.Indent > 0)
                ImGui.Unindent(item.Indent * 12f);
        }
    }
}
