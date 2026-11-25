using NUnit.Framework;
using Shouldly;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeDockTransferTests
{
    [Test]
    public void CapturedToolStateRequiresAStateAwareDestination()
    {
        object context = new();
        TestWorldContext world = new();
        SceneNode sourceNode = new("Dock source");
        SceneNode destinationNode = new("Dock destination");
        sourceNode.SetWorldContext(world);
        destinationNode.SetWorldContext(world);
        NativeDockWindowRegistration source = sourceNode.AddComponent<NativeDockWindowRegistration>()!;
        NativeDockWindowRegistration destination = destinationNode.AddComponent<NativeDockWindowRegistration>()!;
        string sourceKey = Guid.NewGuid().ToString("N");
        string destinationKey = Guid.NewGuid().ToString("N");
        int deliveries = 0;
        try
        {
            NativeDockPanelStateValues values = NativeDockPanelStateValues.Capture(
                new NativeDockLayout(), NativeDockPanel.Console);
            source.Configure(sourceKey, _ => true, context,
                capturePanelState: _ => new NativeDockPanelTransferState(values, () => true));
            destination.Configure(destinationKey, _ => { deliveries++; return true; }, context,
                receivePanelAtStack: (_, _) => { deliveries++; return true; });
            NativeDockWindowRegistry.TransferToAnotherWindow(source, NativeDockPanel.Console)
                .ShouldBe(NativeDockTransferResult.NoTarget);
            NativeDockWindowRegistry.TransferToWindow(source, destination,
                NativeDockPanel.Console, "target").ShouldBe(NativeDockTransferResult.Rejected);
            deliveries.ShouldBe(0);

            NativeDockPanelStateValues scene = NativeDockPanelStateValues.Capture(
                new NativeDockLayout(), NativeDockPanel.Scene);
            source.Configure(sourceKey, _ => true, context,
                capturePanelState: _ => new NativeDockPanelTransferState(scene, () => true));
            NativeDockWindowRegistry.TransferToAnotherWindow(source, NativeDockPanel.Scene)
                .ShouldBe(NativeDockTransferResult.Committed);
            NativeDockWindowRegistry.TransferToWindow(source, destination,
                NativeDockPanel.Scene, "target").ShouldBe(NativeDockTransferResult.Committed);
            deliveries.ShouldBe(2);
        }
        finally
        {
            sourceNode.Destroy(true);
            destinationNode.Destroy(true);
        }
    }

    [Test]
    public void SourceCaptureReentryCannotMutateDestinationBeforeRegistryRejects()
    {
        object context = new();
        TestWorldContext world = new();
        SceneNode sourceNode = new("Dock source");
        SceneNode destinationNode = new("Dock destination");
        sourceNode.SetWorldContext(world);
        destinationNode.SetWorldContext(world);
        NativeDockWindowRegistration source = sourceNode.AddComponent<NativeDockWindowRegistration>()!;
        NativeDockWindowRegistration destination = destinationNode.AddComponent<NativeDockWindowRegistration>()!;
        string sourceKey = Guid.NewGuid().ToString("N");
        string destinationKey = Guid.NewGuid().ToString("N");
        int deliveries = 0;
        try
        {
            NativeDockPanelStateValues values = NativeDockPanelStateValues.Capture(
                new NativeDockLayout(), NativeDockPanel.Console);
            source.Configure(sourceKey, _ => true, context,
                capturePanelState: _ =>
                {
                    source.Configure(sourceKey, _ => true, context);
                    return new NativeDockPanelTransferState(values, () => true);
                });
            destination.Configure(destinationKey, _ => true, context,
                receivePanelAtStack: (_, _) => true,
                receivePanelState: (_, _) => { deliveries++; return true; },
                receivePanelStateAtStack: (_, _, _) => { deliveries++; return true; });
            NativeDockWindowRegistry.TransferToAnotherWindow(source, NativeDockPanel.Console)
                .ShouldBe(NativeDockTransferResult.Rejected);
            deliveries.ShouldBe(0);

            source.Configure(sourceKey, _ => true, context,
                capturePanelState: _ =>
                {
                    destination.Configure(destinationKey, _ => true, context,
                        receivePanelAtStack: (_, _) => true);
                    return new NativeDockPanelTransferState(values, () => true);
                });
            NativeDockWindowRegistry.TransferToWindow(source, destination,
                NativeDockPanel.Console, "target").ShouldBe(NativeDockTransferResult.Rejected);
            deliveries.ShouldBe(0);
        }
        finally
        {
            sourceNode.Destroy(true);
            destinationNode.Destroy(true);
        }
    }

    [Test]
    public void DestinationMustConfirmCommitBeforeSourceCloses()
    {
        object context = new();
        SceneNode sourceNode = new("Dock source");
        SceneNode destinationNode = new("Dock destination");
        TestWorldContext world = new();
        sourceNode.SetWorldContext(world);
        destinationNode.SetWorldContext(world);
        NativeDockWindowRegistration source = sourceNode.AddComponent<NativeDockWindowRegistration>()!;
        NativeDockWindowRegistration destination = destinationNode.AddComponent<NativeDockWindowRegistration>()!;
        string sourceKey = "source:" + Guid.NewGuid().ToString("N");
        string destinationKey = "destination:" + Guid.NewGuid().ToString("N");
        NativeDockTree sourceTree = new NativeDockLayout().Sanitize().DockTree!;
        NativeDockTree destinationTree = new()
        {
            Root = new NativeDockNode { Tabs = [NativeDockPanel.Scene], Selected = NativeDockPanel.Scene },
        };
        destinationTree.Sanitize();
        try
        {
            source.Configure(sourceKey, _ => true, context);
            destination.Configure(destinationKey, _ => false, context);
            NativeDockWindowRegistry.TransferToAnotherWindow(source, NativeDockPanel.Assets)
                .ShouldBe(NativeDockTransferResult.Rejected);
            Contains(sourceTree, NativeDockPanel.Assets).ShouldBeTrue();

            destination.Configure(destinationKey, _ => throw new InvalidOperationException("Destination failed"), context);
            NativeDockWindowRegistry.TransferToAnotherWindow(source, NativeDockPanel.Assets)
                .ShouldBe(NativeDockTransferResult.Rejected);
            Contains(sourceTree, NativeDockPanel.Assets).ShouldBeTrue();

            source.Configure(sourceKey, _ => true, context);
            destination.Configure(destinationKey, _ =>
            {
                destination.Configure(destinationKey, _ => true, context);
                return true;
            }, context);
            NativeDockWindowRegistry.TransferToAnotherWindow(source, NativeDockPanel.Assets)
                .ShouldBe(NativeDockTransferResult.Rejected);
            Contains(sourceTree, NativeDockPanel.Assets).ShouldBeTrue();
            Contains(destinationTree, NativeDockPanel.Assets).ShouldBeFalse();

            destination.Configure(destinationKey, panel =>
                destinationTree.Move(panel, destinationTree.Root.Id, int.MaxValue), context);
            NativeDockTransferResult result = NativeDockWindowRegistry.TransferToAnotherWindow(
                source, NativeDockPanel.Assets);
            if (result == NativeDockTransferResult.Committed)
                sourceTree.Close(NativeDockPanel.Assets).ShouldBeTrue();
            result.ShouldBe(NativeDockTransferResult.Committed);
            sourceTree.Sanitize();
            Contains(sourceTree, NativeDockPanel.Assets).ShouldBeFalse();
            sourceTree.ClosedPanels.ShouldContain(NativeDockPanel.Assets);
            Contains(destinationTree, NativeDockPanel.Assets).ShouldBeTrue();
            sourceTree.Reopen(NativeDockPanel.Assets, sourceTree.Stacks().First().Id).ShouldBeTrue();
            Contains(sourceTree, NativeDockPanel.Assets).ShouldBeTrue();
        }
        finally
        {
            sourceNode.Destroy(true);
            destinationNode.Destroy(true);
            NativeDockWindowRegistry.Unregister(sourceKey, source);
            NativeDockWindowRegistry.Unregister(destinationKey, destination);
        }
    }

    [Test]
    public void ReentrantOwnerReplacementCannotAuthorizeSourceClosure()
    {
        object context = new();
        SceneNode sourceNode = new("Dock source");
        SceneNode destinationNode = new("Dock destination");
        TestWorldContext world = new();
        sourceNode.SetWorldContext(world);
        destinationNode.SetWorldContext(world);
        NativeDockWindowRegistration source = sourceNode.AddComponent<NativeDockWindowRegistration>()!;
        NativeDockWindowRegistration destination = destinationNode.AddComponent<NativeDockWindowRegistration>()!;
        string sourceKey = "source:" + Guid.NewGuid().ToString("N");
        string destinationKey = "destination:" + Guid.NewGuid().ToString("N");
        string replacementKey = "replacement:" + Guid.NewGuid().ToString("N");
        NativeDockTree sourceTree = new NativeDockLayout().Sanitize().DockTree!;
        NativeDockTree destinationTree = new()
        {
            Root = new NativeDockNode { Tabs = [NativeDockPanel.Scene], Selected = NativeDockPanel.Scene },
        };
        destinationTree.Sanitize();
        try
        {
            source.Configure(sourceKey, _ => true, context);
            destination.Configure(destinationKey, panel =>
            {
                destinationTree.Move(panel, destinationTree.Root.Id, int.MaxValue).ShouldBeTrue();
                source.Configure(replacementKey, _ => true, context);
                return true;
            }, context);
            NativeDockWindowRegistry.TransferToAnotherWindow(source, NativeDockPanel.Assets)
                .ShouldBe(NativeDockTransferResult.Rejected);
            Contains(sourceTree, NativeDockPanel.Assets).ShouldBeTrue();
            Contains(destinationTree, NativeDockPanel.Assets).ShouldBeTrue();

            source.Configure(sourceKey, _ => true, context);
            destination.Configure(destinationKey, _ =>
            {
                destination.Configure(replacementKey, _ => true, context);
                return true;
            }, context);
            NativeDockWindowRegistry.TransferToAnotherWindow(source, NativeDockPanel.Assets)
                .ShouldBe(NativeDockTransferResult.Rejected);
            Contains(sourceTree, NativeDockPanel.Assets).ShouldBeTrue();
        }
        finally
        {
            sourceNode.Destroy(true);
            destinationNode.Destroy(true);
            NativeDockWindowRegistry.Unregister(sourceKey, source);
            NativeDockWindowRegistry.Unregister(destinationKey, destination);
            NativeDockWindowRegistry.Unregister(replacementKey, source);
            NativeDockWindowRegistry.Unregister(replacementKey, destination);
        }
    }

    [Test]
    public void DifferentWorldContextHasNoEligibleDestination()
    {
        SceneNode sourceNode = new("Dock source");
        SceneNode destinationNode = new("Dock destination");
        TestWorldContext world = new();
        sourceNode.SetWorldContext(world);
        destinationNode.SetWorldContext(world);
        NativeDockWindowRegistration source = sourceNode.AddComponent<NativeDockWindowRegistration>()!;
        NativeDockWindowRegistration destination = destinationNode.AddComponent<NativeDockWindowRegistration>()!;
        string sourceKey = "source:" + Guid.NewGuid().ToString("N");
        string destinationKey = "destination:" + Guid.NewGuid().ToString("N");
        try
        {
            source.Configure(sourceKey, _ => true, new object());
            destination.Configure(destinationKey, _ => true, new object());
            NativeDockWindowRegistry.TransferToAnotherWindow(source, NativeDockPanel.Assets)
                .ShouldBe(NativeDockTransferResult.NoTarget);
        }
        finally
        {
            sourceNode.Destroy(true);
            destinationNode.Destroy(true);
            NativeDockWindowRegistry.Unregister(sourceKey, source);
            NativeDockWindowRegistry.Unregister(destinationKey, destination);
        }
    }

    private static bool Contains(NativeDockTree tree, string panel)
        => tree.Stacks().Any(stack => stack.Tabs.Contains(panel));
}
