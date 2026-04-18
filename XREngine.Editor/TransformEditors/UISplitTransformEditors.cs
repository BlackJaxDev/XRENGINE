using System.Collections.Generic;
using ImGuiNET;
using XREngine.Rendering.UI;
using XREngine.Scene.Transforms;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor.TransformEditors;

public sealed class UIDualSplitTransformEditor : IXRTransformEditor
{
    private enum SplitSizingMode
    {
        Proportional,
        FixedFirst,
        FixedSecond,
    }

    public void DrawInspector(TransformBase transform, HashSet<object> visited)
    {
        if (transform is not UIDualSplitTransform split)
        {
            DrawDefaultTransformInspector(transform, visited);
            return;
        }

        new UIBoundableTransformEditor().DrawInspector(split, visited);

        string transformLabel = TransformEditorUtil.GetTransformDisplayName(split);
        DrawSplitSettings(split, transformLabel);
    }

    private static void DrawSplitSettings(UIDualSplitTransform split, string transformLabel)
    {
        if (!ImGui.CollapsingHeader("Split", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawCheckbox("Vertical Split", split.VerticalSplit, value => split.VerticalSplit = value, $"Set Split Axis {transformLabel}", split);

        SplitSizingMode sizingMode = GetSizingMode(split);
        DrawEnumCombo("Sizing Mode", sizingMode, value => ApplySizingMode(split, value), $"Set Split Sizing Mode {transformLabel}", split);

        if (GetSizingMode(split) == SplitSizingMode.Proportional)
            DrawFloat("Split Percent", split.SplitPercent, 0.01f, value => split.SplitPercent = value, $"Set Split Percent {transformLabel}", split);
        else
            DrawOptionalFloat("Fixed Size Override", split.FixedSize, value => split.FixedSize = value, $"Set Fixed Split Size {transformLabel}", split);

        DrawFloat("Splitter Size", split.SplitterSize, 0.25f, value => split.SplitterSize = value, $"Set Splitter Size {transformLabel}", split);
        DrawCheckbox("User Resizable", split.CanUserResize, value => split.CanUserResize = value, $"Toggle User Resize {transformLabel}", split);

        DrawReadOnlyField("First Child", DescribeBoundable(split.First));
        DrawReadOnlyField("Second Child", DescribeBoundable(split.Second));

        ImGui.Spacing();
    }

    private static SplitSizingMode GetSizingMode(UIDualSplitTransform split)
        => split.FirstFixedSize switch
        {
            true => SplitSizingMode.FixedFirst,
            false => SplitSizingMode.FixedSecond,
            null => SplitSizingMode.Proportional,
        };

    private static void ApplySizingMode(UIDualSplitTransform split, SplitSizingMode sizingMode)
    {
        split.FirstFixedSize = sizingMode switch
        {
            SplitSizingMode.FixedFirst => true,
            SplitSizingMode.FixedSecond => false,
            _ => null,
        };
    }

    private static string DescribeBoundable(UIBoundableTransform? transform)
        => transform is null ? "None" : $"{transform.Name} ({transform.GetType().Name})";
}

public sealed class UIMultiSplitTransformEditor : IXRTransformEditor
{
    public void DrawInspector(TransformBase transform, HashSet<object> visited)
    {
        if (transform is not UIMultiSplitTransform split)
        {
            DrawDefaultTransformInspector(transform, visited);
            return;
        }

        new UIBoundableTransformEditor().DrawInspector(split, visited);

        string transformLabel = TransformEditorUtil.GetTransformDisplayName(split);
        DrawSplitSettings(split, transformLabel);
    }

    private static void DrawSplitSettings(UIMultiSplitTransform split, string transformLabel)
    {
        if (!ImGui.CollapsingHeader("Multi Split", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawEnumCombo("Arrangement", split.Arrangement, value => split.Arrangement = value, $"Set Split Arrangement {transformLabel}", split);
        DrawFloat("Splitter Size", split.SplitterSize, 0.25f, value => split.SplitterSize = value, $"Set Splitter Size {transformLabel}", split);
        DrawCheckbox("User Resizable", split.CanUserResize, value => split.CanUserResize = value, $"Toggle User Resize {transformLabel}", split);

        DrawOptionalFloat("Fixed Size First", split.FixedSizeFirst, value => split.FixedSizeFirst = value, $"Set First Fixed Size {transformLabel}", split);
        DrawOptionalFloat("Fixed Size Second", split.FixedSizeSecond, value => split.FixedSizeSecond = value, $"Set Second Fixed Size {transformLabel}", split);
        DrawFloat("Split Percent First", split.SplitPercentFirst, 0.01f, value => split.SplitPercentFirst = value, $"Set First Split Percent {transformLabel}", split);

        if (split.Arrangement is UISplitArrangement.LeftMiddleRight or UISplitArrangement.TopMiddleBottom)
            DrawFloat("Split Percent Second", split.SplitPercentSecond, 0.01f, value => split.SplitPercentSecond = value, $"Set Second Split Percent {transformLabel}", split);

        DrawReadOnlyField("Child Count", split.Children.Count.ToString());

        ImGui.Spacing();
    }
}