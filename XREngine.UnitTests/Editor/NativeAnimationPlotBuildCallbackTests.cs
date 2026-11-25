using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeAnimationPlotBuildCallbackTests
{
    [Test]
    public void RetiredBuildStaysHiddenAndHasNoPointerMetadata()
    {
        using UITestShaderScope shaders = new();
        object? previousAsset = NativeAssetSelection.SelectedTarget;
        SceneNode node = new("Animation") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        AnimationPanel panel = node.AddComponent<AnimationPanel>()!;
        AnimationClip clip = new(new AnimationMember("Root"))
        {
            LengthInSeconds = 2
        };
        UIRetainedPlot plot = new(node, "Plot");
        AnimationPlotInputComponent input = plot.Root
            .AddComponent<AnimationPlotInputComponent>()!;
        input.Panel = panel;
        typeof(AnimationPanel).GetField("_plot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(panel, plot);
        typeof(AnimationPanel).GetField("_plotInput", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(panel, input);
        typeof(AnimationPanel).GetField("_displayedClip", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(panel, clip);
        UIBoundableTransform root = plot.Root.GetTransformAs<UIBoundableTransform>(true)!;
        typeof(UIBoundableTransform).GetField("_actualSize",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(root, new Vector2(180, 180));
        FieldInfo sourceRevision = typeof(AnimationPanel).GetField("_sourceRevision",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool interrupted = false;
        root.PropertyChanged += (_, change) =>
        {
            if (interrupted || change.PropertyName != nameof(root.Visibility) ||
                root.Visibility != EVisibility.Hidden)
                return;
            interrupted = true;
            sourceRevision.SetValue(panel, (ulong)sourceRevision.GetValue(panel)! + 1);
        };
        try
        {
            typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
                .SetValue(null, clip);
            typeof(AnimationPanel).GetMethod("RefreshPlot",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [clip]);

            Assert.That(interrupted, Is.True);
            Assert.That(root.Visibility, Is.EqualTo(EVisibility.Hidden));
            Assert.That(typeof(AnimationPanel).GetField("_plotSnapshot",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel), Is.Null);
            Assert.That(panel.CapturePlotPointerMetadata(new Vector2(180, 180)), Is.Null);
        }
        finally
        {
            typeof(NativeAssetSelection).GetProperty(nameof(NativeAssetSelection.SelectedTarget))!
                .SetValue(null, previousAsset);
            node.Destroy(true);
        }
    }
}
