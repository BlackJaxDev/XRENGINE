using NUnit.Framework;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public class InspectorPlayRestoreOwnershipTests
{
    [Test]
    public void TargetWrite_PropertyChangingSelectsNewerTarget_RejectsOuterWrite()
    {
        SceneNode node = new("Inspector") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        InspectorPanel panel = node.AddComponent<InspectorPanel>()!;
        object?[] older = [new object()];
        object?[] newer = [new object()];
        bool replaced = false;
        panel.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName != nameof(InspectorPanel.InspectedObjects) || replaced)
                return;
            replaced = true;
            panel.InspectedObjects = newer;
        };

        try
        {
            panel.InspectedObjects = older;

            Assert.That(replaced, Is.True);
            Assert.That(panel.InspectedObjects, Is.SameAs(newer));
        }
        finally
        {
            node.Destroy(true);
        }
    }

    [Test]
    public void TargetWrite_PropertyChangedSelectsNewerTarget_KeepsNewerTarget()
    {
        SceneNode node = new("Inspector") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        InspectorPanel panel = node.AddComponent<InspectorPanel>()!;
        object?[] older = [new object()];
        object?[] newer = [new object()];
        bool replaced = false;
        panel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName != nameof(InspectorPanel.InspectedObjects) || replaced)
                return;
            replaced = true;
            panel.InspectedObjects = newer;
        };

        try
        {
            panel.InspectedObjects = older;

            Assert.That(replaced, Is.True);
            Assert.That(panel.InspectedObjects, Is.SameAs(newer));
        }
        finally
        {
            node.Destroy(true);
        }
    }
}
