using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;

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
        for (int i = 0; i < content.Items.Count; i++)
        {
            RenderPipelineWorkbenchInspectorItem item = content.Items[i];
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

        object? editable = (object?)content.Command ?? content.Container;
        if (editable is null)
            return;

        ImGui.Spacing();
        ImGui.SeparatorText("Properties");
        if (_workbench.Model.Pipeline is { } pipeline && !IsWorkbenchPipelineEditable(pipeline))
            ImGui.TextDisabled("Code pipelines rebuild their commands from code. Changes here last until the next rebuild.");

        if (!ReferenceEquals(_workbenchInspectorTargetObject, editable))
        {
            _workbenchInspectorTargetObject = editable;
            _workbenchInspectorTargets = new InspectorTargetSet([editable], editable.GetType());
        }

        _workbenchInspectorVisited.Clear();
        DrawInspectableObject(_workbenchInspectorTargets!, content.PropertyEditorId, _workbenchInspectorVisited);
    }
}
