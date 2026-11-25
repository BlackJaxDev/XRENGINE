using NUnit.Framework;
using Shouldly;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeDockPointedTransferTests
{
    [Test]
    public void ExactDestinationAndStackReceiveThePanel()
    {
        TestWorldContext world = new();
        SceneNode sourceNode = new("Dock source");
        SceneNode otherNode = new("Other dock destination");
        SceneNode pointedNode = new("Pointed dock destination");
        sourceNode.SetWorldContext(world);
        otherNode.SetWorldContext(world);
        pointedNode.SetWorldContext(world);
        NativeDockWindowRegistration source = sourceNode.AddComponent<NativeDockWindowRegistration>()!;
        NativeDockWindowRegistration other = otherNode.AddComponent<NativeDockWindowRegistration>()!;
        NativeDockWindowRegistration pointed = pointedNode.AddComponent<NativeDockWindowRegistration>()!;
        string sourceKey = "source:" + Guid.NewGuid().ToString("N");
        string otherKey = "other:" + Guid.NewGuid().ToString("N");
        string pointedKey = "pointed:" + Guid.NewGuid().ToString("N");
        int otherReceives = 0;
        int pointedReceives = 0;
        try
        {
            source.Configure(sourceKey, _ => true, world);
            other.Configure(otherKey, _ => true, world,
                receivePanelAtStack: (_, _) => { otherReceives++; return true; });
            pointed.Configure(pointedKey, _ => true, world,
                receivePanelAtStack: (panel, stack) =>
                {
                    pointedReceives++;
                    panel.ShouldBe(NativeDockPanel.Scene);
                    stack.ShouldBe("selected-stack");
                    return true;
                });
            NativeDockWindowRegistry.TransferToWindow(source, pointed,
                NativeDockPanel.Scene, "selected-stack")
                .ShouldBe(NativeDockTransferResult.Committed);
            pointedReceives.ShouldBe(1);
            otherReceives.ShouldBe(0);
            NativeDockWindowRegistry.TransferToWindow(source, source,
                NativeDockPanel.Scene, "selected-stack")
                .ShouldBe(NativeDockTransferResult.Rejected);
            NativeDockWindowRegistry.TransferToWindow(source, pointed,
                NativeDockPanel.Scene, string.Empty)
                .ShouldBe(NativeDockTransferResult.Rejected);
        }
        finally
        {
            sourceNode.Destroy(true);
            otherNode.Destroy(true);
            pointedNode.Destroy(true);
            NativeDockWindowRegistry.Unregister(sourceKey, source);
            NativeDockWindowRegistry.Unregister(otherKey, other);
            NativeDockWindowRegistry.Unregister(pointedKey, pointed);
        }
    }

    [Test]
    public void CallbackReplacementRejectsPointedReceipt()
    {
        TestWorldContext world = new();
        SceneNode sourceNode = new("Dock source");
        SceneNode targetNode = new("Dock target");
        sourceNode.SetWorldContext(world);
        targetNode.SetWorldContext(world);
        NativeDockWindowRegistration source = sourceNode.AddComponent<NativeDockWindowRegistration>()!;
        NativeDockWindowRegistration target = targetNode.AddComponent<NativeDockWindowRegistration>()!;
        string sourceKey = "source:" + Guid.NewGuid().ToString("N");
        string targetKey = "target:" + Guid.NewGuid().ToString("N");
        try
        {
            source.Configure(sourceKey, _ => true, world);
            target.Configure(targetKey, _ => true, world,
                receivePanelAtStack: (_, _) =>
                {
                    target.Configure(targetKey, _ => true, world,
                        receivePanelAtStack: (_, _) => true);
                    return true;
                });
            NativeDockWindowRegistry.TransferToWindow(source, target,
                NativeDockPanel.Assets, "target-stack")
                .ShouldBe(NativeDockTransferResult.Rejected);

            target.Configure(targetKey, _ => true, world,
                receivePanelAtStack: (_, _) =>
                {
                    source.Configure(sourceKey, _ => true, world);
                    return true;
                });
            NativeDockWindowRegistry.TransferToWindow(source, target,
                NativeDockPanel.Assets, "target-stack")
                .ShouldBe(NativeDockTransferResult.Rejected);
        }
        finally
        {
            sourceNode.Destroy(true);
            targetNode.Destroy(true);
            NativeDockWindowRegistry.Unregister(sourceKey, source);
            NativeDockWindowRegistry.Unregister(targetKey, target);
        }
    }
}
