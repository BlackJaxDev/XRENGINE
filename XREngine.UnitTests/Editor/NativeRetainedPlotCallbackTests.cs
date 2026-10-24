using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeRetainedPlotCallbackTests
{
    private UITestShaderScope _shaderScope = null!;
    [SetUp] public void SetUp() => _shaderScope = new UITestShaderScope();
    [TearDown] public void TearDown() => _shaderScope.Dispose();

    [Test]
    public void AddLineStopsAfterTransformCallbackInvalidatesBuild()
    {
        SceneNode parent = new("Plot") { IsActiveSelf = false };
        parent.SetTransform<UIBoundableTransform>();
        UIRetainedPlot plot = new(parent, "Animation");
        UIBoundableTransform root = plot.Root.GetTransformAs<UIBoundableTransform>(true)!;
        typeof(UIBoundableTransform).GetField("_actualSize",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(root, new Vector2(100, 100));
        try
        {
            plot.Begin();
            plot.AddLine(new Vector2(5, 10), new Vector2(90, 10), Vector4.One);
            plot.End();
            var strokes = (List<(UIBoundableTransform Transform,
                UIMaterialComponent Material)>)typeof(UIRetainedPlot)
                .GetField("_strokes", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(plot)!;
            Assert.That(strokes.Count, Is.EqualTo(1));
            UIBoundableTransform stroke = strokes[0].Transform;
            float? oldHeight = stroke.Height;
            bool current = true;
            stroke.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(UIBoundableTransform.Width))
                    current = false;
            };

            plot.Begin();
            plot.AddLine(new Vector2(5, 20), new Vector2(75, 20),
                Vector4.One, 9, () => current);

            Assert.That(current, Is.False);
            Assert.That(stroke.Height, Is.EqualTo(oldHeight));
        }
        finally { parent.Destroy(true); }
    }

    [Test]
    public void EndStopsAfterMaterialCallbackInvalidatesBuild()
    {
        SceneNode parent = new("Plot") { IsActiveSelf = false };
        parent.SetTransform<UIBoundableTransform>();
        UIRetainedPlot plot = new(parent, "Animation");
        var strokes = (List<(UIBoundableTransform Transform,
            UIMaterialComponent Material)>)typeof(UIRetainedPlot)
            .GetField("_strokes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(plot)!;
        for (int index = 0; index < 2; index++)
        {
            SceneNode stroke = plot.Root.NewChildWithTransform(out UIBoundableTransform transform,
                "Stroke");
            UIMaterialComponent material = stroke.AddComponent<UIMaterialComponent>()!;
            material.SetBatchSolidColor(Vector4.One);
            strokes.Add((transform, material));
        }
        bool current = true;
        strokes[0].Material.PropertyChanged += (_, _) => current = false;
        try
        {
            plot.Begin();
            plot.End(() => current);
            int used = (int)typeof(UIRetainedPlot)
                .GetField("_used", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(plot)!;
            Assert.That(current, Is.False);
            Assert.That(used, Is.EqualTo(1));
        }
        finally { parent.Destroy(true); }
    }
}
