using System.Collections.Generic;
using ImGuiNET;
using XREngine.Rendering.UI;
using XREngine.Scene.Transforms;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor.TransformEditors;

public sealed class UIScrollableTransformEditor : IXRTransformEditor
{
    public void DrawInspector(TransformBase transform, HashSet<object> visited)
    {
        if (transform is not UIScrollableTransform scrollable)
        {
            DrawDefaultTransformInspector(transform, visited);
            return;
        }

        new UIBoundableTransformEditor().DrawInspector(scrollable, visited);

        string transformLabel = TransformEditorUtil.GetTransformDisplayName(scrollable);
        DrawScrollSettings(scrollable, transformLabel);
    }

    private static void DrawScrollSettings(UIScrollableTransform scrollable, string transformLabel)
    {
        if (!ImGui.CollapsingHeader("Scroll", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawCheckbox("Scrollable", scrollable.Scrollable, value => scrollable.Scrollable = value, $"Toggle Scrollability {transformLabel}", scrollable);

        ImGui.BeginDisabled(!scrollable.Scrollable);
        DrawCheckbox("Horizontal", scrollable.ScrollableX, value => scrollable.ScrollableX = value, $"Toggle Horizontal Scroll {transformLabel}", scrollable);
        DrawCheckbox("Vertical", scrollable.ScrollableY, value => scrollable.ScrollableY = value, $"Toggle Vertical Scroll {transformLabel}", scrollable);
        DrawFloat("Horizontal Margin", scrollable.ScrollableXMargin, 0.25f, value => scrollable.ScrollableXMargin = value, $"Set Horizontal Scroll Margin {transformLabel}", scrollable);
        DrawFloat("Vertical Margin", scrollable.ScrollableYMargin, 0.25f, value => scrollable.ScrollableYMargin = value, $"Set Vertical Scroll Margin {transformLabel}", scrollable);
        DrawFloat("Horizontal Min", scrollable.ScrollableXMin, 0.25f, value => scrollable.ScrollableXMin = value, $"Set Horizontal Scroll Min {transformLabel}", scrollable);
        DrawFloat("Horizontal Max", scrollable.ScrollableXMax, 0.25f, value => scrollable.ScrollableXMax = value, $"Set Horizontal Scroll Max {transformLabel}", scrollable);
        DrawFloat("Vertical Min", scrollable.ScrollableYMin, 0.25f, value => scrollable.ScrollableYMin = value, $"Set Vertical Scroll Min {transformLabel}", scrollable);
        DrawFloat("Vertical Max", scrollable.ScrollableYMax, 0.25f, value => scrollable.ScrollableYMax = value, $"Set Vertical Scroll Max {transformLabel}", scrollable);
        ImGui.EndDisabled();

        ImGui.Spacing();
    }
}

public sealed class UIFittedTransformEditor : IXRTransformEditor
{
    public void DrawInspector(TransformBase transform, HashSet<object> visited)
    {
        if (transform is not UIFittedTransform fitted)
        {
            DrawDefaultTransformInspector(transform, visited);
            return;
        }

        new UIBoundableTransformEditor().DrawInspector(fitted, visited);

        string transformLabel = TransformEditorUtil.GetTransformDisplayName(fitted);
        DrawFitSettings(fitted, transformLabel);
    }

    private static void DrawFitSettings(UIFittedTransform fitted, string transformLabel)
    {
        if (!ImGui.CollapsingHeader("Fit", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawEnumCombo("Fit Type", fitted.FitType, value => fitted.FitType = value, $"Set Fit Type {transformLabel}", fitted);

        ImGui.Spacing();
    }
}