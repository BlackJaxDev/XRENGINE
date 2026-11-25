using System.Reflection;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Physics.Jitter2;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public class InspectorHiddenPlayStateTests
{
    [Test]
    public void HiddenInspector_KeepsPlaySnapshotUntilActive_AndReleasesSubscriptionOnDestroy()
    {
        SceneNode target = new("Inspected node");
        XRWorld authoredWorld = new("Inspector world", new XRScene("Scene", target));
        using RuntimeWorld world = new(new JitterScene(), authoredWorld);
        SceneNode panelNode = new("Hidden inspector") { IsActiveSelf = false };
        panelNode.SetTransform<UIBoundableTransform>();
        InspectorPanel panel = panelNode.AddComponent<InspectorPanel>()!;
        typeof(InspectorPanel).GetField("_inspectedObjects", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(panel, new object?[] { target });
        var snapshotField = typeof(InspectorPanel).GetField("_playInspectedTargets",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var configuration = RuntimePlayModeController.Current.Configuration;
        var previousMode = configuration.StateRestorationMode;
        configuration.StateRestorationMode = EStateRestorationMode.SerializeAndRestore;
        try
        {
            Invoke(panel, "SubscribePlayModeTransitions");
            Invoke(panel, "OnComponentDeactivated");

            RuntimePlayModeController.Current.RaisePreEnterPlay();
            Assert.That(snapshotField.GetValue(panel), Is.Not.Null);
            RuntimePlayModeController.Current.RaisePostExitPlay();
            Assert.That(snapshotField.GetValue(panel), Is.Not.Null,
                "A hidden panel must keep the GUID snapshot until it can rebuild its view.");

            configuration.StateRestorationMode = EStateRestorationMode.PersistChanges;
            RuntimePlayModeController.Current.RaisePostExitPlay();
            Assert.That(snapshotField.GetValue(panel), Is.Null,
                "PersistChanges must not restore edit-mode targets.");

            panelNode.Destroy(true);
            RuntimePlayModeController.Current.RaisePreEnterPlay();
            Assert.That(snapshotField.GetValue(panel), Is.Null,
                "A destroyed panel must not receive play-mode events.");
        }
        finally
        {
            configuration.StateRestorationMode = previousMode;
            panelNode.Destroy(true);
        }
    }

    private static void Invoke(InspectorPanel panel, string name)
        => typeof(InspectorPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, null);
}
