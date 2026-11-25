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
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void ExistingNodeFactoryFailureAbortsOnlyItsUninsertedClaim(bool virtualScroll, bool queueHide)
    {
        SceneNode root = CreateCanvas(out _);
        XRComponent? failed = null;
        var failure = new InvalidOperationException("Existing-owner factory failed.");
        try
        {
            var menu = CreateMenu(root);
            void OnCreated(XRComponent component)
            {
                if (!MatchesExistingFactory(component, menu, virtualScroll)) return;
                failed = component;
                if (queueHide) menu.Hide();
                throw failure;
            }
            XRComponent.ComponentCreated += OnCreated;
            try { Should.Throw<InvalidOperationException>(() => ShowExistingFactory(menu, virtualScroll)).ShouldBeSameAs(failure); }
            finally { XRComponent.ComponentCreated -= OnCreated; }
            failed.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            menu.SceneNode.IsDestroyed.ShouldBeFalse();
            if (!virtualScroll) menu.SceneNode.GetComponent<UIMaterialComponent>().ShouldBeNull();
            menu.FailedComponentCreations.ShouldBeEmpty();
            menu.IsOpen.ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ExistingNodeFactoryTransferPreservesForeignOwnerAndChildren(bool virtualScroll)
    {
        SceneNode root = CreateCanvas(out _);
        XRComponent? transferred = null;
        SceneNode? owner = null, borrowed = null;
        SceneNode other = new(root, "Other owner", new UIBoundableTransform());
        try
        {
            var menu = CreateMenu(root);
            void OnCreated(XRComponent component)
            {
                if (!MatchesExistingFactory(component, menu, virtualScroll)) return;
                transferred = component;
                owner = component.SceneNode;
                borrowed = new(owner, "Borrowed child", new UIBoundableTransform());
                owner.Parent = other;
            }
            XRComponent.ComponentCreated += OnCreated;
            try { Should.Throw<InvalidOperationException>(() => ShowExistingFactory(menu, virtualScroll)); }
            finally { XRComponent.ComponentCreated -= OnCreated; }
            transferred.ShouldNotBeNull().IsDestroyed.ShouldBeFalse();
            owner.ShouldNotBeNull().Parent.ShouldBeSameAs(other);
            borrowed.ShouldNotBeNull().Parent.ShouldBeSameAs(owner);
            borrowed.IsDestroyed.ShouldBeFalse();
            owner.Components.Contains(transferred).ShouldBeFalse();
            menu.FailedComponentCreations.Single().Component.ShouldBeSameAs(transferred);
            menu.FailedConstructionLeaves.ShouldBeEmpty();
            menu.IsOpen.ShouldBeFalse();
        }
        finally
        {
            if (transferred is { IsDestroyed: false }) transferred.Destroy(true);
            root.Destroy(true);
        }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void CommittedFactoryFinishesActivationBeforeQueuedCancellation(bool virtualScroll, bool replace)
    {
        SceneNode root = CreateCanvas(out _);
        XRComponent? original = null;
        bool activeAtRetirement = false;
        bool requested = false;
        try
        {
            var menu = CreateMenu(root);
            void OnInserted(XRComponent component)
            {
                if (!ReferenceEquals(component, original)) return;
                component.SceneNode.Components.PostAnythingAdded -= OnInserted;
                requested = true;
                if (replace) menu.Show(Vector2.Zero, new ContextMenuItem("Replacement", null));
                else menu.Hide();
            }
            void OnCreated(XRComponent component)
            {
                if (original is not null || !MatchesExistingFactory(component, menu, virtualScroll)) return;
                original = component;
                component.SceneNode.Components.PostAnythingAdded += OnInserted;
                component.SceneNode.Destroying += _ =>
                {
                    activeAtRetirement = component.IsComponentActivated;
                    return true;
                };
            }
            XRComponent.ComponentCreated += OnCreated;
            try { ShowExistingFactory(menu, virtualScroll); }
            finally { XRComponent.ComponentCreated -= OnCreated; }
            requested.ShouldBeTrue();
            original.ShouldNotBeNull();
            if (virtualScroll)
            {
                activeAtRetirement.ShouldBeTrue();
                original.IsDestroyQueued.ShouldBeTrue();
                original.IsComponentActivated.ShouldBeFalse();
                original.SceneNode.IsDestroyed.ShouldBeTrue();
                ((UIVirtualPopupScrollComponent)original).List.ShouldBeNull();
            }
            else
            {
                original.IsComponentActivated.ShouldBeTrue();
                original.IsDestroyed.ShouldBeFalse();
                menu.SceneNode.GetComponent<UIMaterialComponent>().ShouldBeSameAs(original);
                ((UIMaterialComponent)original).ClipToBounds.ShouldBe(replace);
            }
            menu.FailedComponentCreations.ShouldBeEmpty();
            menu.IsOpen.ShouldBe(replace);
            if (replace) Rows(menu).Single().Item!.Label.ShouldBe("Replacement");
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void FailedCommittedBackdropIsRetainedAndCannotBeReusedAsReady()
    {
        SceneNode root = CreateCanvas(out _);
        UIMaterialComponent? failed = null;
        var failure = new InvalidOperationException("Insertion observer failed.");
        try
        {
            var menu = CreateMenu(root);
            void OnInserted(XRComponent component)
            {
                if (component is not UIMaterialComponent background) return;
                menu.SceneNode.Components.PostAnythingAdded -= OnInserted;
                failed = background;
                throw failure;
            }
            menu.SceneNode.Components.PostAnythingAdded += OnInserted;
            try { Should.Throw<InvalidOperationException>(() => ShowExistingFactory(menu, false)).ShouldBeSameAs(failure); }
            finally { menu.SceneNode.Components.PostAnythingAdded -= OnInserted; }
            failed.ShouldNotBeNull().IsDestroyed.ShouldBeFalse();
            failed.IsComponentActivated.ShouldBeFalse();
            menu.SceneNode.GetComponent<UIMaterialComponent>().ShouldBeSameAs(failed);
            menu.FailedComponentCreations.Single().CanAbortClaimed.ShouldBeFalse();
            Should.Throw<InvalidOperationException>(() => ShowExistingFactory(menu, false));
            failed.IsComponentActivated.ShouldBeFalse();
            failed.ClipToBounds.ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void VirtualScrollFactoryRejectsProviderReplacementAndAwayBack(bool restoreProvider)
    {
        SceneNode root = CreateCanvas(out _);
        UIVirtualPopupScrollComponent? stale = null;
        var provider = new CountingVirtualListProvider(10);
        try
        {
            var menu = CreateMenu(root);
            void OnCreated(XRComponent component)
            {
                if (component is not UIVirtualPopupScrollComponent scroll) return;
                stale = scroll;
                var list = (UIListTransform)scroll.Transform;
                list.VirtualProvider = new CountingVirtualListProvider(3);
                if (restoreProvider) list.VirtualProvider = provider;
            }
            XRComponent.ComponentCreated += OnCreated;
            try { Should.Throw<InvalidOperationException>(() => menu.ShowVirtual(Vector2.Zero, provider)); }
            finally { XRComponent.ComponentCreated -= OnCreated; }
            stale.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            stale.List.ShouldBeNull();
            menu.VirtualList.ShouldBeNull();
            menu.FailedComponentCreations.ShouldBeEmpty();
            menu.IsOpen.ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void VirtualProviderSetupRejectsReplacementBeforePublishingScroll(bool restoreProvider)
    {
        SceneNode root = CreateCanvas(out _);
        var provider = new CountingVirtualListProvider(10);
        int factories = 0;
        bool changed = false;
        try
        {
            var menu = CreateMenu(root);
            void OnRoot(TransformBase transform)
            {
                var list = (UIListTransform)transform;
                list.PropertyChanged += (_, args) =>
                {
                    if (changed || args.PropertyName != nameof(UIListTransform.VirtualProvider)) return;
                    changed = true;
                    list.VirtualProvider = new CountingVirtualListProvider(3);
                    if (restoreProvider) list.VirtualProvider = provider;
                };
            }
            void OnCreated(XRComponent component) { if (component is UIVirtualPopupScrollComponent) factories++; }
            menu.Transform.Children.PostAnythingAdded += OnRoot;
            XRComponent.ComponentCreated += OnCreated;
            try { Should.Throw<InvalidOperationException>(() => menu.ShowVirtual(Vector2.Zero, provider)); }
            finally
            {
                menu.Transform.Children.PostAnythingAdded -= OnRoot;
                XRComponent.ComponentCreated -= OnCreated;
            }
            changed.ShouldBeTrue();
            factories.ShouldBe(0);
            menu.VirtualList.ShouldBeNull();
            menu.IsOpen.ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void SuccessfulVirtualScrollBindsTheExactPublishedList()
    {
        SceneNode root = CreateCanvas(out _);
        try
        {
            var menu = CreateMenu(root);
            var provider = new CountingVirtualListProvider(10);
            menu.ShowVirtual(Vector2.Zero, provider);
            UIListTransform list = menu.VirtualList.ShouldNotBeNull();
            list.VirtualProvider.ShouldBeSameAs(provider);
            list.SceneNode!.GetComponent<UIVirtualPopupScrollComponent>()!.List.ShouldBeSameAs(list);
            menu.FailedComponentCreations.ShouldBeEmpty();
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PreInsertionCancellationAbortsTheOldClaimAndAllowsReplacement(bool virtualScroll)
    {
        SceneNode root = CreateCanvas(out _);
        XRComponent? original = null;
        try
        {
            var menu = CreateMenu(root);
            void OnCreated(XRComponent component)
            {
                if (original is not null || !MatchesExistingFactory(component, menu, virtualScroll)) return;
                original = component;
                menu.Show(Vector2.Zero, new ContextMenuItem("Replacement", null));
            }
            XRComponent.ComponentCreated += OnCreated;
            try { ShowExistingFactory(menu, virtualScroll); }
            finally { XRComponent.ComponentCreated -= OnCreated; }
            original.ShouldNotBeNull().IsDestroyed.ShouldBeTrue();
            menu.FailedComponentCreations.ShouldBeEmpty();
            Rows(menu).Single().Item!.Label.ShouldBe("Replacement");
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BackdropConfigurationCancellationOrFailurePreservesCompletedCreation(bool throwFailure)
    {
        SceneNode root = CreateCanvas(out _);
        UIMaterialComponent? backdrop = null;
        var failure = new InvalidOperationException("Backdrop configuration failed.");
        int rows = 0;
        bool called = false;
        try
        {
            var menu = CreateMenu(root);
            void OnCreated(XRComponent component)
            {
                if (component is UIContextMenuItemComponent) rows++;
                if (backdrop is not null || !MatchesExistingFactory(component, menu, false)) return;
                backdrop = (UIMaterialComponent)component;
                backdrop.PropertyChanged += (_, args) =>
                {
                    if (called || args.PropertyName != nameof(UIMaterialComponent.ClipToBounds)) return;
                    called = true;
                    menu.Hide();
                    if (throwFailure) throw failure;
                };
            }
            XRComponent.ComponentCreated += OnCreated;
            try
            {
                if (throwFailure)
                    Should.Throw<InvalidOperationException>(() => ShowExistingFactory(menu, false)).ShouldBeSameAs(failure);
                else ShowExistingFactory(menu, false);
            }
            finally { XRComponent.ComponentCreated -= OnCreated; }
            called.ShouldBeTrue();
            rows.ShouldBe(0);
            backdrop.ShouldNotBeNull().IsDestroyed.ShouldBeFalse();
            backdrop.IsComponentActivated.ShouldBeTrue();
            menu.FailedComponentCreations.Count.ShouldBe(throwFailure ? 1 : 0);
            menu.IsOpen.ShouldBeFalse();
            menu.Show(Vector2.Zero, new ContextMenuItem("Recovered", null));
            menu.SceneNode.GetComponent<UIMaterialComponent>().ShouldBeSameAs(backdrop);
            Rows(menu).Single().Item!.Label.ShouldBe("Recovered");
        }
        finally { root.Destroy(true); }
    }

    private static bool MatchesExistingFactory(XRComponent component, UIContextMenuComponent menu, bool virtualScroll)
        => virtualScroll ? component is UIVirtualPopupScrollComponent :
            component is UIMaterialComponent && ReferenceEquals(component.SceneNode, menu.SceneNode);

    private static void ShowExistingFactory(UIContextMenuComponent menu, bool virtualScroll)
    {
        if (virtualScroll) menu.ShowVirtual(Vector2.Zero, new CountingVirtualListProvider(10));
        else menu.Show(Vector2.Zero, new ContextMenuItem("Action", null));
    }
}
