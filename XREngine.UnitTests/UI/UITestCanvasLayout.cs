using System;
using System.Threading;
using Shouldly;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.UI;

/// <summary>
/// Runs the worker layout for a test canvas and applies the accepted result to its transforms.
/// Retained canvases apply layout only from published worker results, so tests wait for them.
/// </summary>
internal static class UITestCanvasLayout
{
    /// <summary>Creates a scene node whose transform is a canvas of the given size.</summary>
    public static SceneNode CreateCanvasRoot(string name, float width, float height, out UICanvasTransform canvas)
    {
        SceneNode root = new(name);
        canvas = root.SetTransform<UICanvasTransform>();
        canvas.Width = width;
        canvas.Height = height;
        return root;
    }

    /// <summary>
    /// Requests layout, waits until the canvas accepts a current result, and then recomputes
    /// world matrices so spatial queries read the new geometry.
    /// </summary>
    public static void Settle(UICanvasTransform canvas)
    {
        canvas.UpdateLayoutAsync();
        bool applied = false;
        SpinWait.SpinUntil(() =>
        {
            applied |= canvas.PumpLayoutResults();
            if (canvas.IsLayoutInvalidated)
            {
                applied = false;
                canvas.UpdateLayoutAsync();
            }
            return applied && !canvas.IsLayoutInvalidated;
        }, TimeSpan.FromSeconds(5)).ShouldBeTrue("The test canvas must apply a current layout result.");
        RecalculateSubtree(canvas);
    }

    private static void RecalculateSubtree(TransformBase transform)
    {
        transform.RecalculateMatrices(forceWorldRecalc: true);
        var children = transform.Children;
        for (int index = 0; index < children.Count; index++)
            RecalculateSubtree(children[index]);
    }
}
