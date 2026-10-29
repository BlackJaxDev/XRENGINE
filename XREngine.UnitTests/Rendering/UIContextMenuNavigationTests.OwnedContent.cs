using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

public sealed partial class UIContextMenuNavigationTests
{
    [Test]
    public void OwnedContentRetiresNestedLeavesOnce()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode parent = null!, child = null!;
            int parentDestroying = 0, childDestroying = 0;
            menu.ShowOwnedContent(Vector2.Zero, 40, builder =>
            {
                parent = builder.Add(builder.Root, "Parent", new UIBoundableTransform(), _ => { });
                child = builder.Add(parent, "Child", new UIBoundableTransform(), _ => { });
            });
            parent.Destroying += _ => { parentDestroying++; return true; };
            child.Destroying += _ => { childDestroying++; return true; };

            menu.Hide();
            menu.Hide();

            parent.IsDestroyed.ShouldBeTrue();
            child.IsDestroyed.ShouldBeTrue();
            parentDestroying.ShouldBe(1);
            childDestroying.ShouldBe(1);
            menu.FailedOwnedContents.ShouldBeEmpty();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void OwnedBuilderFailureKeepsOriginalExceptionAndRetiresAttachedLeaf()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode? leaf = null;
            InvalidOperationException failure = new("Builder failed.");
            Should.Throw<InvalidOperationException>(() => menu.ShowOwnedContent(Vector2.Zero, 20, builder =>
            {
                leaf = builder.Add(builder.Root, "Owned", new UIBoundableTransform(), _ => { });
                throw failure;
            })).ShouldBeSameAs(failure);
            leaf.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            menu.FailedOwnedContents.ShouldBeEmpty();
            menu.IsOpen.ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void TransferredOwnedBranchKeepsItsDescendant()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode parent = null!, child = null!;
            menu.ShowOwnedContent(Vector2.Zero, 40, builder =>
            {
                parent = builder.Add(builder.Root, "Parent", new UIBoundableTransform(), _ => { });
                child = builder.Add(parent, "Child", new UIBoundableTransform(), _ => { });
            });
            parent.Parent = root;

            menu.Hide();

            parent.IsDestroyed.ShouldBeFalse();
            child.IsDestroyed.ShouldBeFalse();
            child.Parent.ShouldBeSameAs(parent);
            menu.FailedOwnedContents.ShouldBeEmpty();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void TransferredParentRejectsTheNextGuardedFactory()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode? moved = null;
            bool configured = false;
            Should.Throw<InvalidOperationException>(() => menu.ShowOwnedContent(Vector2.Zero, 40, builder =>
            {
                moved = builder.Add(builder.Root, "Moved", new UIBoundableTransform(), _ => { });
                moved.Parent = root;
                builder.Add(moved, "Rejected", new UIBoundableTransform(), _ => configured = true);
            }));

            configured.ShouldBeFalse();
            moved.ShouldNotBeNull().IsDestroyed.ShouldBeFalse();
            moved.Parent.ShouldBeSameAs(root);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void GuardedWriteStopsAfterItsFirstCallbackTransfersTheLeaf()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode? transferred = null;
            UIBoundableTransform transform = new();
            Should.Throw<InvalidOperationException>(() => menu.ShowOwnedContent(Vector2.Zero, 20,
                builder => builder.Add(builder.Root, "Transferred", transform, leaf =>
                {
                    transferred = leaf.Node;
                    transform.PropertyChanged += (_, change) =>
                    {
                        if (change.PropertyName == nameof(UIBoundableTransform.Height))
                            transferred.Parent = root;
                    };
                    leaf.Write(() => transform.Height = 24);
                    leaf.Write(() => transform.Width = 137);
                })));
            transform.Width.ShouldBeNull();
            transferred.ShouldNotBeNull().IsDestroyed.ShouldBeFalse();
            transferred.Parent.ShouldBeSameAs(root);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void RootTransferDuringDeepCleanupKeepsLaterTrackedLeaves()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode contentRoot = null!, parent = null!, middle = null!, deepest = null!, sibling = null!;
            menu.ShowOwnedContent(Vector2.Zero, 60, builder =>
            {
                contentRoot = builder.Root;
                parent = builder.Add(contentRoot, "Parent", new UIBoundableTransform(), _ => { });
                sibling = builder.Add(parent, "Sibling", new UIBoundableTransform(), _ => { });
                middle = builder.Add(parent, "Middle", new UIBoundableTransform(), _ => { });
                deepest = builder.Add(middle, "Deepest", new UIBoundableTransform(), _ => { });
            });
            deepest.Destroying += _ => { contentRoot.Parent = root; return true; };

            menu.Hide();

            deepest.IsDestroyed.ShouldBeTrue();
            middle.IsDestroyed.ShouldBeFalse();
            sibling.IsDestroyed.ShouldBeFalse();
            parent.IsDestroyed.ShouldBeFalse();
            contentRoot.IsDestroyed.ShouldBeFalse();
            contentRoot.Parent.ShouldBeSameAs(root);
            menu.FailedOwnedContents.ShouldBeEmpty();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void LeafConstructionRejectsAwayAndBackAttachment()
    {
        SceneNode parent = new("Parent", new UIBoundableTransform());
        try
        {
            UIContextMenuLeafConstruction construction = new(parent, () => { });
            construction.Attach();
            construction.Node.Parent = null;
            construction.Node.Parent = parent;

            construction.OwnsAttachedNode().ShouldBeFalse();
            construction.Node.IsDestroyed.ShouldBeFalse();
        }
        finally { parent.Destroy(true); }
    }

    [Test]
    public void ForeignChildBlocksOwnedRetirementAndKeepsFailedReceipt()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode leaf = null!;
            menu.ShowOwnedContent(Vector2.Zero, 20, builder =>
                leaf = builder.Add(builder.Root, "Owned", new UIBoundableTransform(), _ => { }));
            SceneNode foreign = new(leaf);

            menu.Hide();

            leaf.IsDestroyed.ShouldBeFalse();
            foreign.IsDestroyed.ShouldBeFalse();
            menu.FailedOwnedContents.Count.ShouldBe(1);
            menu.FailedOwnedContents[0].Failure.ShouldNotBeNull();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void VetoedOwnedDestructionIsNotRetriedByRepeatedHide()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            UIContextMenuComponent menu = CreateMenu(root);
            SceneNode leaf = null!;
            menu.ShowOwnedContent(Vector2.Zero, 20, builder =>
                leaf = builder.Add(builder.Root, "Owned", new UIBoundableTransform(), _ => { }));
            int attempts = 0;
            bool Veto(XRObjectBase _) { attempts++; return false; }
            leaf.Destroying += Veto;
            menu.Hide();
            menu.Hide();
            attempts.ShouldBe(1);
            leaf.IsDestroyed.ShouldBeFalse();
            menu.FailedOwnedContents.Count.ShouldBe(1);
            leaf.Destroying -= Veto;
        }
        finally { root.Destroy(true); }
    }
}
