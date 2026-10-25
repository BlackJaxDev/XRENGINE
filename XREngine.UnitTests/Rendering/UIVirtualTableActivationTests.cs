using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIVirtualTableActivationTests
{
    [TestCase("unchanged")]
    [TestCase("recycle")]
    [TestCase("refresh")]
    [TestCase("provider")]
    [TestCase("selection")]
    [TestCase("selection-round-trip")]
    [TestCase("row")]
    [TestCase("destroy")]
    [TestCase("disabled")]
    [TestCase("nested")]
    public void ActivationCannotCrossASelectionCallbackOwnershipChange(string change)
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("Table actions", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        UIVirtualTableRefreshSource source = new();
        UIVirtualTableProvider<string> table = CreateTable(source);
        UIListTransform list = new() { ItemSize = 20, VirtualOverscan = 0 };
        _ = new SceneNode(root, "Rows", list);
        list.VirtualProvider = table;
        try
        {
            list.SynchronizeVirtualRows(20);
            list.TryGetRealizedVirtualRow(11, out var bounds, out _).ShouldBeTrue();
            UIVirtualTableRowComponent row = bounds!.SceneNode!.GetComponent<UIVirtualTableRowComponent>()!;
            List<ulong> activated = [];
            table.Activated += activated.Add;
            bool changed = false;
            table.SelectionChanged += id =>
            {
                if (changed || id != 11) return;
                changed = true;
                switch (change)
                {
                    case "recycle": source.Replace(22, "Replacement"); list.SynchronizeVirtualRows(20); break;
                    case "refresh": source.AdvanceRevision(); list.SynchronizeVirtualRows(20); break;
                    case "provider": list.VirtualProvider = null; break;
                    case "selection": table.SelectedId = 22; break;
                    case "selection-round-trip": table.SelectedId = 22; table.SelectedId = 11; break;
                    case "row": row.Generate(); break;
                    case "destroy": row.SceneNode.Destroy(true); break;
                    case "disabled": row.IsEnabled = false; break;
                    case "nested":
                        source.Replace(22, "Replacement");
                        list.SynchronizeVirtualRows(20);
                        list.TryGetRealizedVirtualRow(22, out var replacement, out _).ShouldBeTrue();
                        replacement!.SceneNode!.GetComponent<UIVirtualTableRowComponent>()!.OnInteract();
                        break;
                }
            };

            row.OnInteract();

            if (change == "unchanged") activated.ShouldBe([11UL]);
            else if (change == "nested") activated.ShouldBe([22UL]);
            else activated.ShouldBeEmpty();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void SelectionVisualCallbackDoesNotPublishTheSupersededSelection()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("Table selection", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        UIVirtualTableProvider<string> table = CreateTable(new UIVirtualTableRefreshSource());
        UIListTransform list = new() { ItemSize = 20, VirtualOverscan = 0 };
        _ = new SceneNode(root, "Rows", list);
        list.VirtualProvider = table;
        try
        {
            list.SynchronizeVirtualRows(20);
            list.TryGetRealizedVirtualRow(11, out var bounds, out _).ShouldBeTrue();
            UIVirtualTableRowComponent row = bounds!.SceneNode!.GetComponent<UIVirtualTableRowComponent>()!;
            bool changed = false;
            void ChangeSelection(object? sender, IXRPropertyChangedEventArgs args)
            {
                if (changed || args.PropertyName != nameof(UIVirtualTableRowComponent.IsSelected) || !row.IsSelected) return;
                changed = true;
                table.SelectedId = 22;
            }
            row.PropertyChanged += ChangeSelection;
            List<ulong?> selected = [];
            table.SelectionChanged += selected.Add;

            table.SelectedId = 11;

            table.SelectedId.ShouldBe(22UL);
            selected.ShouldBe([22UL]);
            row.IsSelected.ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    private static UIVirtualTableProvider<string> CreateTable(UIVirtualTableRefreshSource source)
        => new(source, new UIVirtualTableColumn<string>("value", "Value", 100,
            parent => new SceneNode(parent).SetTransform<UIBoundableTransform>(),
            (_, _, _) => { }, _ => { }));

    [Test]
    public void SelectionChangingCallbackCannotHighlightARecycledRow()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("Table selection rebind", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        UIVirtualTableRefreshSource source = new();
        UIVirtualTableProvider<string> table = CreateTable(source);
        UIListTransform list = new() { ItemSize = 20, VirtualOverscan = 0 };
        _ = new SceneNode(root, "Rows", list);
        list.VirtualProvider = table;
        try
        {
            list.SynchronizeVirtualRows(20);
            list.TryGetRealizedVirtualRow(11, out var bounds, out _).ShouldBeTrue();
            UIVirtualTableRowComponent row = bounds!.SceneNode!.GetComponent<UIVirtualTableRowComponent>()!;
            bool changed = false;
            row.PropertyChanging += (_, args) =>
            {
                if (changed || args.PropertyName != nameof(UIVirtualTableRowComponent.IsSelected)) return;
                changed = true;
                source.Replace(22, "Replacement");
                list.SynchronizeVirtualRows(20);
            };

            table.SelectedId = 11;

            row.BoundItemId.ShouldBe(22UL);
            row.IsSelected.ShouldBeFalse();
            table.SelectedId.ShouldBe(11UL);
        }
        finally { root.Destroy(true); }
    }
}
