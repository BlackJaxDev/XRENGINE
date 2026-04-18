using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ImGuiNET;
using XREngine.Rendering.UI;
using XREngine.Scene.Transforms;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor.TransformEditors;

public sealed class UIListTransformEditor : IXRTransformEditor
{
    public void DrawInspector(TransformBase transform, HashSet<object> visited)
    {
        if (transform is not UIListTransform list)
        {
            DrawDefaultTransformInspector(transform, visited);
            return;
        }

        new UIBoundableTransformEditor().DrawInspector(list, visited);

        string transformLabel = TransformEditorUtil.GetTransformDisplayName(list);
        DrawListSettings(list, transformLabel);
    }

    private static void DrawListSettings(UIListTransform list, string transformLabel)
    {
        if (!ImGui.CollapsingHeader("List", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawCheckbox("Display Horizontal", list.DisplayHorizontal, value => list.DisplayHorizontal = value, $"Set List Orientation {transformLabel}", list);
        DrawOptionalFloat("Item Size", list.ItemSize, value => list.ItemSize = value, $"Set Item Size {transformLabel}", list);
        DrawFloat("Item Spacing", list.ItemSpacing, 0.25f, value => list.ItemSpacing = value, $"Set Item Spacing {transformLabel}", list);
        DrawEnumCombo("Item Alignment", list.ItemAlignment, value => list.ItemAlignment = value, $"Set Item Alignment {transformLabel}", list);
        DrawFloat("Content Scroll Offset", list.ContentScrollOffset, 0.25f, value => list.ContentScrollOffset = value, $"Set Content Scroll Offset {transformLabel}", list);
        DrawCheckbox("Virtualized", list.Virtual, value => list.Virtual = value, $"Toggle List Virtualization {transformLabel}", list);

        if (list.Virtual)
        {
            DrawFloat("Upper Virtual Bound", list.UpperVirtualBound, 0.25f, value => list.UpperVirtualBound = value, $"Set Upper Virtual Bound {transformLabel}", list);
            DrawFloat("Lower Virtual Bound", list.LowerVirtualBound, 0.25f, value => list.LowerVirtualBound = value, $"Set Lower Virtual Bound {transformLabel}", list);
            DrawReadOnlyField("Virtual Region Size", list.VirtualRegionSize.ToString("0.##", CultureInfo.InvariantCulture));
        }

        DrawReadOnlyField("Child Count", list.Children.Count.ToString());

        ImGui.Spacing();
    }
}

public sealed class UIGridTransformEditor : IXRTransformEditor
{
    public void DrawInspector(TransformBase transform, HashSet<object> visited)
    {
        if (transform is not UIGridTransform grid)
        {
            DrawDefaultTransformInspector(transform, visited);
            return;
        }

        new UIBoundableTransformEditor().DrawInspector(grid, visited);

        string transformLabel = TransformEditorUtil.GetTransformDisplayName(grid);
        DrawGridSettings(grid, transformLabel);
    }

    private static void DrawGridSettings(UIGridTransform grid, string transformLabel)
    {
        if (!ImGui.CollapsingHeader("Grid", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawCheckbox("Invert Y", grid.InvertY, value => grid.InvertY = value, $"Invert Grid Y {transformLabel}", grid);
        DrawReadOnlyField("Rows", grid.Rows.Count.ToString(CultureInfo.InvariantCulture));
        DrawReadOnlyField("Columns", grid.Columns.Count.ToString(CultureInfo.InvariantCulture));
        DrawReadOnlyField("Cells", (grid.Rows.Count * grid.Columns.Count).ToString(CultureInfo.InvariantCulture));
        DrawReadOnlyField("Auto Rows", grid.Rows.Count(x => x.NeedsAutoSizing).ToString(CultureInfo.InvariantCulture));
        DrawReadOnlyField("Auto Columns", grid.Columns.Count(x => x.NeedsAutoSizing).ToString(CultureInfo.InvariantCulture));
        ImGui.TextDisabled("Edit row and column sizing definitions in Advanced.");

        ImGui.Spacing();
    }
}