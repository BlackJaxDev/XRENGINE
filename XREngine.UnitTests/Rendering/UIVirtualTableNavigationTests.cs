using XREngine.Rendering.UI.State;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Input.Devices;
using XREngine.Rendering.UI;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIVirtualTableNavigationTests
{
    private UITestShaderScope _shaderScope = null!;
    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown] public void RestoreShaderAssets() => _shaderScope.Dispose();

    [Test]
    public void IndexedDPadRequestWaitsForPresentedRowAndSelectsOnlyAcceptedLease()
    {
        SceneNode root = new("Table navigation", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UICanvasInputComponent input = root.AddComponent<UICanvasInputComponent>()!;
            UIVirtualTableNavigationSource source = new(1_000_000);
            UIVirtualTableProvider<int> table = CreateTable(source);
            UIListTransform list = new() { ItemSize = 20, VirtualOverscan = 0 };
            _ = new SceneNode(root, "List", list);
            list.VirtualProvider = table;
            list.SynchronizeVirtualRows(20);
            list.TryGetRealizedVirtualRow(1, out UIBoundableTransform? firstRow,
                out _).ShouldBeTrue();
            UIVirtualTableRowComponent first = firstRow!.SceneNode!.GetComponent<UIVirtualTableRowComponent>()!;
            ulong requested = 0;
            first.NavigationRequested += (_, target) => requested = target;
            first.IsFocused = true;

            first.TryConsumeFocusDirection(new Vector2(0, -1)).ShouldBeTrue();
            requested.ShouldBe(2UL);
            input.HasPendingFocusRequest.ShouldBeTrue();
            UITestTreeKeyRegistration keys = new();
            first.RegisterInput(keys);
            keys.Press(EKey.Down).ShouldBeTrue();
            requested.ShouldBe(2UL);
            table.SelectedId.ShouldBeNull();
            source.IndexReads.ShouldBeLessThan(16);
            source.IdReads.ShouldBeLessThan(40);

            list.SynchronizeVirtualRows(20);
            list.TryGetRealizedVirtualRow(2, out UIBoundableTransform? secondRow,
                out UIVirtualRowLease secondLease).ShouldBeTrue();
            UIVirtualTableRowComponent second = secondRow!.SceneNode!.GetComponent<UIVirtualTableRowComponent>()!;
            first.IsFocused.ShouldBeFalse();
            BoundingRectangleF bounds = new(Vector2.Zero, new Vector2(100));
            UIHitRecord hit = new(second.RenderInfo2D, second.RenderInfo2D.MembershipId,
                second.UITransform.ElementIdentity, second.InteractionRevision,
                Matrix4x4.Identity, new Vector2(20), bounds, null,
                3, 2, 4, 3, 0, 0, true, false,
                FocusScopeIdentity: canvas.CanvasTransform.ElementIdentity,
                VirtualListIdentity: list.ElementIdentity,
                VirtualRowIdentity: secondRow.ElementIdentity,
                VirtualRowLease: secondLease,
                VirtualProviderIncarnation: list.VirtualProviderIncarnation);
            UIPublication packet = new(1, canvas.PublicationLifetime, 1, 1,
                list.ParentCanvas!.ChangeJournal.Revision, default,
                new UIElementIdentity[] { canvas.CanvasTransform.ElementIdentity, list.ElementIdentity,
                    secondRow.ElementIdentity, second.UITransform.ElementIdentity }, [hit], [],
                bounds, Matrix4x4.Identity, Matrix4x4.Identity);
            canvas.MarkPresented(canvas, packet);
            packet.Release();
            RefreshPresentedInput(input);

            input.FocusedComponent.ShouldBeSameAs(second);
            input.HasPendingFocusRequest.ShouldBeFalse();
            table.SelectedId.ShouldBe(2UL);
            source.IndexReads.ShouldBeLessThan(32);
            source.IdReads.ShouldBeLessThan(80);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void RecycledRowAndFocusedEditorDoNotNavigateAsOldRow()
    {
        SceneNode root = new("Table navigation", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        try
        {
            _ = root.AddComponent<UICanvasComponent>()!;
            _ = root.AddComponent<UICanvasInputComponent>()!;
            UIVirtualTableNavigationSource source = new(10);
            UIVirtualTableProvider<int> table = CreateTable(source);
            UIListTransform list = new() { ItemSize = 20, VirtualOverscan = 0 };
            _ = new SceneNode(root, "List", list);
            list.VirtualProvider = table;
            list.SynchronizeVirtualRows(20);
            list.TryGetRealizedVirtualRow(1, out UIBoundableTransform? firstRow, out _).ShouldBeTrue();
            UIVirtualTableRowComponent button = firstRow!.SceneNode!.GetComponent<UIVirtualTableRowComponent>()!;
            UIButtonComponent editor = firstRow.SceneNode.FirstChild!.GetComponent<UIButtonComponent>()!;
            editor.IsFocused = true;
            button.IsFocused = false;
            button.TryConsumeFocusDirection(new Vector2(0, -1)).ShouldBeFalse();
            button.NavigateDown().ShouldBeFalse();

            editor.IsFocused = false;
            button.IsFocused = true;
            list.ContentScrollOffset = 40;
            list.SynchronizeVirtualRows(20);
            button.IsFocused.ShouldBeFalse();
            button.NavigateDown().ShouldBeFalse();
            table.SelectedId.ShouldBeNull();
        }
        finally { root.Destroy(true); }
    }

    private static UIVirtualTableProvider<int> CreateTable(UIVirtualTableNavigationSource source)
        => new(source, new UIVirtualTableColumn<int>("value", "Value", 100,
            parent =>
            {
                SceneNode cell = new(parent);
                UIBoundableTransform bounds = cell.SetTransform<UIBoundableTransform>();
                cell.AddComponent<UIButtonComponent>();
                return bounds;
            },
            (_, _, _) => { }, _ => { }));

    private static void RefreshPresentedInput(UICanvasInputComponent input)
    {
        var method = typeof(UICanvasInputComponent).GetMethod("RefreshPresentedInput",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        UIPublication? publication = (UIPublication?)method.Invoke(input, null);
        publication?.Release();
    }
}
