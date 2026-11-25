using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

public sealed partial class UIContextMenuNavigationTests
{
    private static UIContextMenuItemComponent ShowSubmenuRow(UIContextMenuComponent menu)
    {
        menu.Show(Vector2.Zero, ContextMenuItem.Submenu("More", [new ContextMenuItem("Child", null)]));
        return Rows(menu).Single();
    }

    [Test]
    public void ReusedSubmenuFocusCannotAcceptAnIndependentReplacementShow()
    {
        SceneNode root = CreateCanvas(out var input);
        try
        {
            var menu = CreateMenu(root);
            var row = ShowSubmenuRow(menu);
            menu.UITransform.Visibility = EVisibility.Visible;
            menu.OpenItemSubmenu(row, row.ShowRevision, false).ShouldBeTrue();
            var submenu = menu.OpenSubmenu.ShouldNotBeNull();
            submenu.UITransform.Visibility = EVisibility.Visible;
            var child = Rows(submenu).Single();
            bool replaced = false;
            child.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(child.IsFocused) || !child.IsFocused || replaced) return;
                replaced = true;
                submenu.Show(Vector2.Zero, new ContextMenuItem("Independent", null));
            };
            FocusSource(input, row);
            input.PresentedFocusAccepted = true;
            menu.OpenItemSubmenu(row, row.ShowRevision, true).ShouldBeFalse();
            replaced.ShouldBeTrue();
            menu.OpenSubmenu.ShouldBeNull();
            submenu.SceneNode.IsDestroyed.ShouldBeFalse();
            submenu.OwnerMenu.ShouldBeNull();
            Rows(submenu).Single().Item!.Label.ShouldBe("Independent");
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OwnerTransitionDuringSubmenuShowReleasesTheClosedCandidate(bool replace)
    {
        SceneNode root = CreateCanvas(out _);
        UIContextMenuComponent? candidate = null;
        try
        {
            var menu = CreateMenu(root);
            var row = ShowSubmenuRow(menu);
            void Created(XRComponent component)
            {
                if (component is UIContextMenuComponent submenu) candidate = submenu;
                if (candidate is null || component is not UIMaterialComponent ||
                    !ReferenceEquals(component.SceneNode, candidate.SceneNode)) return;
                if (replace) menu.Show(Vector2.Zero, new ContextMenuItem("Replacement", null));
                else menu.Hide();
            }
            XRComponent.ComponentCreated += Created;
            try { menu.OpenItemSubmenu(row, row.ShowRevision, false).ShouldBeFalse(); }
            finally { XRComponent.ComponentCreated -= Created; }
            candidate.ShouldNotBeNull().SceneNode.IsDestroyed.ShouldBeTrue();
            menu.OpenSubmenu.ShouldBeNull();
            menu.IsOpen.ShouldBe(replace);
            if (replace) Rows(menu).Single().Item!.Label.ShouldBe("Replacement");
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void SubmenuSwitchCannotPublishAnotherCandidateFromDismissal()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            var menu = CreateMenu(root);
            menu.Show(Vector2.Zero,
                ContextMenuItem.Submenu("First", [new ContextMenuItem("One", null)]),
                ContextMenuItem.Submenu("Second", [new ContextMenuItem("Two", null)]));
            var rows = Rows(menu);
            menu.OpenItemSubmenu(rows[0], rows[0].ShowRevision, false).ShouldBeTrue();
            var first = menu.OpenSubmenu.ShouldNotBeNull();
            bool? reentrant = null;
            first.Dismissed += () => reentrant = menu.OpenItemSubmenu(rows[0], rows[0].ShowRevision, false);
            menu.OpenItemSubmenu(rows[1], rows[1].ShowRevision, false).ShouldBeTrue();
            reentrant.ShouldBe(false);
            first.SceneNode.IsDestroyed.ShouldBeTrue();
            Rows(menu.OpenSubmenu.ShouldNotBeNull()).Single().Item!.Label.ShouldBe("Two");
            root.Transform.Children.Count(child => child.SceneNode?.Name == "ContextSubmenu").ShouldBe(1);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void SubmenuFactoryFailureRetiresTheUnpublishedLeaf()
    {
        SceneNode root = CreateCanvas(out _);
        UIContextMenuComponent? candidate = null;
        var failure = new InvalidOperationException("Submenu factory failed.");
        try
        {
            var menu = CreateMenu(root);
            var row = ShowSubmenuRow(menu);
            void Created(XRComponent component)
            {
                if (component is not UIContextMenuComponent submenu) return;
                candidate = submenu;
                submenu.SceneNode.Parent.ShouldBeNull();
                submenu.Transform.ShouldBeOfType<UIBoundableTransform>();
                menu.OpenSubmenu.ShouldBeNull();
                throw failure;
            }
            XRComponent.ComponentCreated += Created;
            try { Should.Throw<InvalidOperationException>(() => menu.OpenItemSubmenu(row, row.ShowRevision, false)).ShouldBeSameAs(failure); }
            finally { XRComponent.ComponentCreated -= Created; }
            candidate.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            candidate.SceneNode.IsDestroyed.ShouldBeTrue();
            menu.FailedConstructionLeaves.ShouldBeEmpty();
            menu.OpenSubmenu.ShouldBeNull();
            menu.IsOpen.ShouldBeTrue();
            menu.OpenItemSubmenu(row, row.ShowRevision, false).ShouldBeTrue();
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SubmenuFactoryFailureKeepsTheErrorAndDrainsTheCapturedOwnerRequest(bool replace)
    {
        SceneNode root = CreateCanvas(out _);
        var failure = new InvalidOperationException("Factory failed after a root request.");
        try
        {
            var menu = CreateMenu(root);
            var row = ShowSubmenuRow(menu);
            void Created(XRComponent component)
            {
                if (component is not UIContextMenuComponent) return;
                if (replace) menu.Show(Vector2.Zero, new ContextMenuItem("Replacement", null));
                else menu.Hide();
                throw failure;
            }
            XRComponent.ComponentCreated += Created;
            try { Should.Throw<InvalidOperationException>(() => menu.OpenItemSubmenu(row, row.ShowRevision, false)).ShouldBeSameAs(failure); }
            finally { XRComponent.ComponentCreated -= Created; }
            menu.IsOpen.ShouldBe(replace);
            menu.OpenSubmenu.ShouldBeNull();
            menu.FailedConstructionLeaves.ShouldBeEmpty();
            if (replace) Rows(menu).Single().Item!.Label.ShouldBe("Replacement");
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void RootTransitionDuringSubmenuConstructionRetiresOnlyTheCandidate(bool atAttachment, bool replace)
    {
        SceneNode root = CreateCanvas(out var input);
        SceneNode? candidate = null;
        try
        {
            var menu = CreateMenu(root);
            var row = ShowSubmenuRow(menu);
            void Change()
            {
                if (replace) menu.Show(Vector2.Zero, new ContextMenuItem("Replacement", null));
                else menu.Hide();
            }
            void Created(XRComponent component)
            {
                if (component is not UIContextMenuComponent submenu) return;
                candidate = submenu.SceneNode;
                if (!atAttachment) Change();
            }
            void Attached(TransformBase transform)
            {
                if (!ReferenceEquals(transform.SceneNode, candidate)) return;
                menu.OpenSubmenu.ShouldBeNull();
                Change();
            }
            XRComponent.ComponentCreated += Created;
            if (atAttachment) root.Transform.Children.PostAnythingAdded += Attached;
            try { menu.OpenItemSubmenu(row, row.ShowRevision, true).ShouldBeFalse(); }
            finally
            {
                XRComponent.ComponentCreated -= Created;
                root.Transform.Children.PostAnythingAdded -= Attached;
            }
            candidate.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            menu.OpenSubmenu.ShouldBeNull();
            menu.FailedConstructionLeaves.ShouldBeEmpty();
            input.PresentedFocusAttempts.ShouldBe(0);
            menu.IsOpen.ShouldBe(replace);
            if (replace) Rows(menu).Single().Item!.Label.ShouldBe("Replacement");
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NestedSubmenuAttachmentQueuesTheAncestorTransition(bool replace)
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            var menu = CreateMenu(root);
            menu.Show(Vector2.Zero, ContextMenuItem.Submenu("More",
                [ContextMenuItem.Submenu("Deeper", [new ContextMenuItem("Leaf", null)])]));
            var row = Rows(menu).Single();
            menu.OpenItemSubmenu(row, row.ShowRevision, false).ShouldBeTrue();
            var first = menu.OpenSubmenu.ShouldNotBeNull();
            var deeper = Rows(first).Single();
            SceneNode? candidate = null;
            void Attached(TransformBase transform)
            {
                if (transform.SceneNode?.Name != "ContextSubmenu") return;
                candidate = transform.SceneNode;
                if (replace) menu.Show(Vector2.Zero, new ContextMenuItem("Replacement", null));
                else menu.Hide();
            }
            root.Transform.Children.PostAnythingAdded += Attached;
            try { first.OpenItemSubmenu(deeper, deeper.ShowRevision, false).ShouldBeFalse(); }
            finally { root.Transform.Children.PostAnythingAdded -= Attached; }
            candidate.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            first.SceneNode.IsDestroyed.ShouldBeTrue();
            menu.OpenSubmenu.ShouldBeNull();
            menu.IsOpen.ShouldBe(replace);
            if (replace) Rows(menu).Single().Item!.Label.ShouldBe("Replacement");
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void TransferredSubmenuAttachmentIsPreservedAndNotPublished()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            var menu = CreateMenu(root);
            var row = ShowSubmenuRow(menu);
            SceneNode destination = root.NewChildWithTransform<UIBoundableTransform>(out _, "Foreign");
            SceneNode? candidate = null;
            void Attached(TransformBase transform)
            {
                if (transform.SceneNode?.Name != "ContextSubmenu") return;
                candidate = transform.SceneNode;
                candidate.Parent = destination;
            }
            root.Transform.Children.PostAnythingAdded += Attached;
            try { Should.Throw<InvalidOperationException>(() => menu.OpenItemSubmenu(row, row.ShowRevision, false)); }
            finally { root.Transform.Children.PostAnythingAdded -= Attached; }
            candidate.ShouldNotBeNull().Parent.ShouldBeSameAs(destination);
            candidate.IsDestroyed.ShouldBeFalse();
            candidate.GetComponent<UIContextMenuComponent>()!.OwnerMenu.ShouldBeNull();
            candidate.Transform.Children.ShouldBeEmpty();
            menu.OpenSubmenu.ShouldBeNull();
            menu.FailedConstructionLeaves.Count.ShouldBe(1);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SubmenuTransferredBeforeOrDuringHideIsNotDestroyed(bool duringHide)
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            var menu = CreateMenu(root);
            var row = ShowSubmenuRow(menu);
            menu.OpenItemSubmenu(row, row.ShowRevision, false).ShouldBeTrue();
            var submenu = menu.OpenSubmenu.ShouldNotBeNull();
            SceneNode destination = root.NewChildWithTransform<UIBoundableTransform>(out _, "Foreign");
            if (duringHide) submenu.Dismissed += () => submenu.SceneNode.Parent = destination;
            else submenu.SceneNode.Parent = destination;
            menu.Hide();
            submenu.SceneNode.Parent.ShouldBeSameAs(destination);
            submenu.SceneNode.IsDestroyed.ShouldBeFalse();
            submenu.IsOpen.ShouldBe(!duringHide);
            menu.OpenSubmenu.ShouldBeNull();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void SubmenuIndependentShowDuringHideSurvivesTheOldOwner()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            var menu = CreateMenu(root);
            var row = ShowSubmenuRow(menu);
            menu.OpenItemSubmenu(row, row.ShowRevision, false).ShouldBeTrue();
            var submenu = menu.OpenSubmenu.ShouldNotBeNull();
            bool replaced = false;
            submenu.Dismissed += () =>
            {
                if (replaced) return;
                replaced = true;
                submenu.Show(Vector2.Zero, new ContextMenuItem("Independent", null));
            };
            menu.Hide();
            submenu.SceneNode.IsDestroyed.ShouldBeFalse();
            submenu.IsOpen.ShouldBeTrue();
            submenu.OwnerMenu.ShouldBeNull();
            Rows(submenu).Single().Item!.Label.ShouldBe("Independent");
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void SubmenuIndependentShowDuringAttachmentIsNotOverwritten()
    {
        SceneNode root = CreateCanvas(out _);
        UIContextMenuComponent? candidate = null;
        try
        {
            var menu = CreateMenu(root);
            var row = ShowSubmenuRow(menu);
            void Attached(TransformBase transform)
            {
                if (transform.SceneNode?.Name != "ContextSubmenu") return;
                candidate = transform.SceneNode.GetComponent<UIContextMenuComponent>()!;
                candidate.Show(Vector2.Zero, new ContextMenuItem("Independent", null));
            }
            root.Transform.Children.PostAnythingAdded += Attached;
            try { Should.Throw<InvalidOperationException>(() => menu.OpenItemSubmenu(row, row.ShowRevision, false)); }
            finally { root.Transform.Children.PostAnythingAdded -= Attached; }
            candidate.ShouldNotBeNull().IsOpen.ShouldBeTrue();
            candidate.OwnerMenu.ShouldBeNull();
            candidate.SceneNode.IsDestroyed.ShouldBeFalse();
            Rows(candidate).Single().Item!.Label.ShouldBe("Independent");
            menu.OpenSubmenu.ShouldBeNull();
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void HiddenSubmenuRetainsCleanupOwnershipAcrossOwnerDeactivation(bool transfer)
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            var menu = CreateMenu(root);
            var row = ShowSubmenuRow(menu);
            menu.OpenItemSubmenu(row, row.ShowRevision, false).ShouldBeTrue();
            var submenu = menu.OpenSubmenu.ShouldNotBeNull();
            menu.SceneNode.IsActiveSelf = false;
            menu.OpenSubmenu.ShouldBeNull();
            submenu.IsOpen.ShouldBeFalse();
            submenu.SceneNode.IsDestroyed.ShouldBeFalse();
            SceneNode destination = root.NewChildWithTransform<UIBoundableTransform>(out _, "Foreign");
            if (transfer) submenu.SceneNode.Parent = destination;
            menu.SceneNode.IsActiveSelf = true;
            row = ShowSubmenuRow(menu);
            menu.OpenItemSubmenu(row, row.ShowRevision, false).ShouldBeTrue();
            submenu.SceneNode.IsDestroyed.ShouldBe(!transfer);
            if (transfer) submenu.SceneNode.Parent.ShouldBeSameAs(destination);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void HiddenSubmenuIndependentShowIsNotRetiredByItsPreviousOwner(bool hideAgain)
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            var menu = CreateMenu(root);
            var row = ShowSubmenuRow(menu);
            menu.OpenItemSubmenu(row, row.ShowRevision, false).ShouldBeTrue();
            var submenu = menu.OpenSubmenu.ShouldNotBeNull();
            menu.SceneNode.IsActiveSelf = false;
            submenu.Show(Vector2.Zero, new ContextMenuItem("Independent", null));
            if (hideAgain) submenu.Hide();
            menu.SceneNode.IsActiveSelf = true;
            row = ShowSubmenuRow(menu);
            menu.OpenItemSubmenu(row, row.ShowRevision, false).ShouldBeTrue();
            submenu.SceneNode.IsDestroyed.ShouldBeFalse();
            submenu.IsOpen.ShouldBe(!hideAgain);
            submenu.OwnerMenu.ShouldBeNull();
            if (!hideAgain) Rows(submenu).Single().Item!.Label.ShouldBe("Independent");
        }
        finally { root.Destroy(true); }
    }
}
