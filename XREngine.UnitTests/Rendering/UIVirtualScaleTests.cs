using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIVirtualScaleTests
{
    [TestCase(100_000)]
    [TestCase(1_000_000)]
    public void LongJumpsKeepRealizationBoundedAndFocusOnPresentedIdentity(int count)
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("Virtual scale", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UICanvasInputComponent input = root.AddComponent<UICanvasInputComponent>()!;
            UIListTransform list = new() { ItemSize = 20, VirtualOverscan = 8 };
            SceneNode listNode = new(root, "List", list);
            FocusVirtualListProvider provider = new(listNode, count);
            list.VirtualProvider = provider;
            list.SynchronizeVirtualRows(800);
            const int capacity = 40 + 1 + 2 * 8;

            foreach (ulong itemId in new ulong[] { (ulong)count, 1, (ulong)count / 2, 1 })
            {
                input.RequestVirtualItemFocus(list, itemId, center: true).ShouldBeTrue();
                list.SynchronizeVirtualRows(800);
                provider.Created.ShouldBeLessThanOrEqualTo(capacity);
                list.ChildCount.ShouldBeLessThanOrEqualTo(capacity);
                list.TryGetRealizedVirtualRow(itemId, out UIBoundableTransform? row,
                    out UIVirtualRowLease lease).ShouldBeTrue();
                row.ShouldNotBeNull();
                UIVirtualButtonComponent button = row!.SceneNode!.FirstChild!
                    .GetComponent<UIVirtualButtonComponent>()!;
                button.CanInteract.ShouldBeTrue();

                // Refresh against the old packet first. A reused subtree cannot
                // inherit focus from the previously displayed item.
                RefreshInput(input);
                input.FocusedComponent.ShouldBeNull();
                BoundingRectangleF bounds = new(Vector2.Zero, new Vector2(800));
                UIHitRecord hit = new(button.RenderInfo2D, button.RenderInfo2D.MembershipId,
                    button.UITransform.ElementIdentity, button.InteractionRevision,
                    Matrix4x4.Identity, new Vector2(20), bounds, null,
                    3, 2, 4, 3, 0, 0, true, false,
                    FocusScopeIdentity: canvas.CanvasTransform.ElementIdentity,
                    VirtualListIdentity: list.ElementIdentity,
                    VirtualRowIdentity: row.ElementIdentity,
                    VirtualRowLease: lease);
                long revision = canvas.CanvasTransform.ChangeJournal.Revision;
                UIPublication packet = new((ulong)revision, canvas.PublicationLifetime, (ulong)revision, (ulong)revision,
                    revision, default,
                    new UIElementIdentity[] { canvas.CanvasTransform.ElementIdentity,
                        list.ElementIdentity, row.ElementIdentity, button.UITransform.ElementIdentity },
                    [hit], [], bounds, Matrix4x4.Identity, Matrix4x4.Identity);
                canvas.MarkPresented(canvas, packet);
                packet.Release();
                RefreshInput(input);
                input.FocusedComponent.ShouldBeSameAs(button);
                list.TryGetVirtualRowLease(row, out UIVirtualRowLease focusedLease).ShouldBeTrue();
                focusedLease.ItemId.ShouldBe(itemId);
            }

            provider.IndexReads.ShouldBe(4);
            // Five bounded synchronizations and a few indexed identity checks.
            provider.ItemReads.ShouldBeLessThanOrEqualTo(5 * capacity * 2 + 16);
        }
        finally { root.Destroy(true); }
    }

    private static void RefreshInput(UICanvasInputComponent input)
    {
        MethodInfo refresh = typeof(UICanvasInputComponent).GetMethod("RefreshPresentedInput",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        UIPublication? packet = (UIPublication?)refresh.Invoke(input, null);
        packet?.Release();
    }
}
