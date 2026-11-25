using NUnit.Framework;
using XREngine.Editor;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Physics.Jitter2;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public class NativeHierarchyWorldRoutingTests
{
    [Test]
    public void QueuedHierarchyAdmission_RejectsForeignWorldAndReboundOwner()
    {
        using RuntimeWorld first = new(new JitterScene());
        using RuntimeWorld second = new(new JitterScene());
        using RuntimeWorldRenderer firstRenderer = new(first, new VisualScene3D());
        using RuntimeWorldRenderer secondRenderer = new(second, new VisualScene3D());
        SceneNode firstRoot = new("First root");
        SceneNode secondRoot = new("Second root");
        first.RootNodes.Add(firstRoot);
        second.RootNodes.Add(secondRoot);
        bool ownerStillBound = true;

        Assert.Multiple(() =>
        {
            Assert.That(EditorImGuiUI.CanApplyNativeHierarchyEdit(firstRoot, first,
                () => ownerStillBound), Is.True);
            Assert.That(EditorImGuiUI.CanApplyNativeHierarchyEdit(firstRoot, second), Is.False);
            Assert.That(EditorImGuiUI.CanApplyNativeHierarchyEdit(secondRoot, first), Is.False);
        });

        ownerStillBound = false;
        Assert.That(EditorImGuiUI.CanApplyNativeHierarchyEdit(firstRoot, first,
            () => ownerStillBound), Is.False);
    }
}
