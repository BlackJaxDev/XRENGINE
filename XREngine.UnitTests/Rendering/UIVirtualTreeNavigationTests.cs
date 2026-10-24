using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Input.Devices;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public class UIVirtualTreeNavigationTests
{
    [Test]
    public void VisibleNeighborsAndEdgesFollowExpansionAndRemoval()
    {
        UIVirtualTreeProvider tree = new(() => new UIBoundableTransform(),
            (_, _, _, _) => { }, (_, _, _) => { });
        tree.Insert(1, 0, 0, "Parent");
        tree.Insert(2, 1, 0, "Child");
        tree.Insert(3, 0, 1, "Sibling");

        Assert.Multiple(() =>
        {
            Assert.That(tree.TryGetAdjacentVisible(1, 1, out ulong next) && next == 3, Is.True);
            Assert.That(tree.TryGetVisibleEdge(true, out ulong first) && first == 1, Is.True);
            Assert.That(tree.TryGetVisibleEdge(false, out ulong last) && last == 3, Is.True);
            Assert.That(tree.TryGetAdjacentVisible(1, int.MaxValue, out _), Is.False);
        });

        tree.SetExpanded(1, true);
        Assert.That(tree.TryGetAdjacentVisible(1, 1, out ulong child) && child == 2, Is.True);
        Assert.That(tree.TryGetAdjacentVisible(2, 1, out ulong sibling) && sibling == 3, Is.True);
        tree.Remove(2);
        Assert.That(tree.TryGetAdjacentVisible(1, 1, out sibling) && sibling == 3, Is.True);
        tree.SetExpanded(1, false);
        Assert.That(tree.TryGetAdjacentVisible(3, 1, out _), Is.False);
    }

    [Test]
    public void FocusedTreeRow_ExpandsAndRequestsStableIdWithoutFocusingUnpresentedRow()
    {
        using UITestShaderScope shader = new();
        SceneNode root = new("Tree canvas", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        UITestCanvasInputProbe input = root.AddComponent<UITestCanvasInputProbe>()!;
        input.VirtualFocusAccepted = true;
        SceneNode listNode = new(root, "Rows");
        UIListTransform list = listNode.SetTransform<UIListTransform>();
        list.ItemSize = 20;
        list.VirtualOverscan = 0;
        UIVirtualTreeProvider? tree = null;
        tree = new UIVirtualTreeProvider(
            () =>
            {
                SceneNode rowNode = new("Tree row");
                UIBoundableTransform row = rowNode.SetTransform<UIBoundableTransform>();
                rowNode.AddComponent<UIVirtualTreeRowButtonComponent>();
                return row;
            },
            (row, item, _, incarnation) => row.SceneNode!
                .GetComponent<UIVirtualTreeRowButtonComponent>()!
                .BindNavigation(tree!, list, item.Id, incarnation),
            (row, _, _) => row.SceneNode!
                .GetComponent<UIVirtualTreeRowButtonComponent>()!
                .UnbindNavigation());
        tree.Insert(1, 0, 0, "Parent");
        tree.Insert(2, 1, 0, "Child");
        tree.Insert(3, 0, 1, "Sibling");
        list.VirtualProvider = tree;
        try
        {
            list.SynchronizeVirtualRows(20);
            root.IsActiveSelf = true;
            list.TryGetRealizedVirtualRow(1, out UIBoundableTransform? row, out _);
            UIVirtualTreeRowButtonComponent button = row!.SceneNode!
                .GetComponent<UIVirtualTreeRowButtonComponent>()!;
            button.IsFocused = true;
            int expansionEvents = 0;
            button.ExpansionChanged += (id, expanded) =>
            {
                if (id == 1 && expanded)
                    expansionEvents++;
            };
            int navigationEvents = 0;
            button.NavigationRequested += (from, target) =>
            {
                if (from == 1 && target == 2)
                    navigationEvents++;
            };

            UITestTreeKeyRegistration keys = new();
            button.RegisterInput(keys);
            Assert.That(keys.KeyCount, Is.EqualTo(8));
            Assert.That(keys.Press(EKey.Right), Is.True);
            Assert.That(tree.TryGetNode(1, out UIVirtualTreeNode? parent) && parent!.IsExpanded, Is.True);
            Assert.That(expansionEvents, Is.EqualTo(1));
            list.SynchronizeVirtualRows(20);
            Assert.That(button.TryConsumeFocusDirection(Vector2.UnitX), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(input.RequestedList, Is.SameAs(list));
                Assert.That(input.RequestedItemId, Is.EqualTo(2UL));
                Assert.That(navigationEvents, Is.EqualTo(1));
                Assert.That(input.FocusedComponent, Is.Null);
            });

            Assert.That(keys.Press(EKey.End), Is.True);
            Assert.That(input.RequestedItemId, Is.EqualTo(3UL));
            keys.Unregister = true;
            button.RegisterInput(keys);
            Assert.That(keys.KeyCount, Is.Zero);

            button.UnbindNavigation();
            Assert.That(button.NavigateDown(), Is.False);
            Assert.That(button.BoundItemId, Is.Zero);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReboundRowWithMatchingNumericLease_DoesNotNotifyPreviousOwner(bool expand)
    {
        using UITestShaderScope shader = new();
        SceneNode root = new("Tree owner transfer", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        UITestCanvasInputProbe input = root.AddComponent<UITestCanvasInputProbe>()!;
        input.VirtualFocusAccepted = true;
        ReentrantListTransform firstList = new SceneNode(root, "First list")
            .SetTransform<ReentrantListTransform>();
        UIListTransform secondList = new SceneNode(root, "Second list")
            .SetTransform<UIListTransform>();
        firstList.ItemSize = secondList.ItemSize = 20;
        firstList.VirtualOverscan = secondList.VirtualOverscan = 0;
        SceneNode rowNode = new("Shared row", new UIBoundableTransform());
        UIBoundableTransform row = rowNode.GetTransformAs<UIBoundableTransform>(true)!;
        UIVirtualTreeRowButtonComponent button = rowNode.AddComponent<UIVirtualTreeRowButtonComponent>()!;
        UIVirtualTreeProvider? firstProvider = null;
        UIVirtualTreeProvider? secondProvider = null;
        firstProvider = new UIVirtualTreeProvider(() => row,
            (_, item, _, incarnation) => button.BindNavigation(firstProvider!, firstList, item.Id, incarnation),
            (_, _, _) => button.UnbindNavigation());
        secondProvider = new UIVirtualTreeProvider(() => row,
            (_, item, _, incarnation) => button.BindNavigation(secondProvider!, secondList, item.Id, incarnation),
            (_, _, _) => button.UnbindNavigation());
        firstProvider.Insert(1, 0, 0, "First");
        firstProvider.Insert(2, expand ? 1UL : 0UL, expand ? 0 : 1, "Second");
        secondProvider.Insert(1, 0, 0, "Replacement");
        firstList.VirtualProvider = firstProvider;
        try
        {
            firstList.SynchronizeVirtualRows(20);
            root.IsActiveSelf = true;
            button.IsFocused = true;
            firstList.TryGetVirtualRowLease(row, out UIVirtualRowLease oldLease).ShouldBeTrue();
            ulong oldProviderIncarnation = firstList.VirtualProviderIncarnation;
            int expansions = 0;
            int navigation = 0;
            button.ExpansionChanged += (_, _) => expansions++;
            button.NavigationRequested += (_, _) => navigation++;
            bool transferred = false;
            void Transfer()
            {
                if (transferred)
                    return;
                transferred = true;
                firstList.VirtualProvider = null;
                secondList.VirtualProvider = secondProvider;
                secondList.SynchronizeVirtualRows(20);
                button.IsFocused = true;
            }
            if (expand)
                firstList.OnInvalidated = Transfer;
            else
                input.OnVirtualFocusRequested = Transfer;

            (expand ? button.NavigateRight() : button.NavigateDown()).ShouldBeTrue();
            transferred.ShouldBeTrue();
            secondList.TryGetVirtualRowLease(row, out UIVirtualRowLease newLease).ShouldBeTrue();
            Assert.Multiple(() =>
            {
                Assert.That(newLease, Is.EqualTo(oldLease));
                Assert.That(secondList.VirtualProviderIncarnation, Is.EqualTo(oldProviderIncarnation));
                Assert.That(button.BoundItemId, Is.EqualTo(1UL));
                Assert.That(expansions, Is.Zero);
                Assert.That(navigation, Is.Zero);
            });

            button.Destroy(true);
            Assert.That(typeof(UIVirtualTreeRowButtonComponent).GetField("NavigationRequested",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(button), Is.Null);
            Assert.That(typeof(UIVirtualTreeRowButtonComponent).GetField("ExpansionChanged",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(button), Is.Null);
        }
        finally { root.Destroy(true); }
    }

    public sealed class ReentrantListTransform : UIListTransform
    {
        public Action? OnInvalidated { get; set; }

        public override void InvalidateMeasure()
        {
            base.InvalidateMeasure();
            OnInvalidated?.Invoke();
        }
    }
}
