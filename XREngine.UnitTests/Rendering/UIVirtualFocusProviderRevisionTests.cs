using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

/// <summary>Checks virtual focus admission after a provider selection changes.</summary>
[TestFixture, NonParallelizable]
public sealed class UIVirtualFocusProviderRevisionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void FocusDispatchRequiresRowsBoundAtThePresentedProviderRevision(bool synchronize)
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("Virtual focus", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UICanvasInputComponent input = root.AddComponent<UICanvasInputComponent>()!;
            UIListTransform list = new() { ItemSize = 20, VirtualOverscan = 0 };
            SceneNode listNode = new(root, "List", list);
            UIVirtualTreeProvider provider = new(
                () =>
                {
                    UIBoundableTransform row = new();
                    SceneNode rowNode = new(listNode, "Row", row);
                    rowNode.AddComponent<UIVirtualButtonComponent>();
                    return row;
                }, (_, _, _, _) => { }, (_, _, _) => { });
            provider.Insert(1, 0, 0, "First");
            list.VirtualProvider = provider;
            list.SynchronizeVirtualRows(20);
            Assert.That(list.TryGetRealizedVirtualRow(1, out UIBoundableTransform? row,
                out UIVirtualRowLease lease), Is.True);
            UIVirtualButtonComponent button = row!.SceneNode!.GetComponent<UIVirtualButtonComponent>()!;
            provider.SetSelected(1, true);
            Assert.That(list.TryGetRealizedVirtualRow(1, out _, out _), Is.False);
            if (synchronize) list.SynchronizeVirtualRows(20);
            bool admitted = false;
            button.GotFocus += _ => admitted = input.IsDispatchingVirtualItemFocus(list, 1, button);
            Assert.That(input.RequestVirtualItemFocus(list, 1), Is.True);
            BoundingRectangleF bounds = new(Vector2.Zero, new Vector2(100));
            UIHitRecord hit = new(button.RenderInfo2D, button.RenderInfo2D.MembershipId,
                button.UITransform.ElementIdentity, button.InteractionRevision,
                Matrix4x4.Identity, new Vector2(20), bounds, null,
                3, 2, 4, 3, 0, 0, true, false,
                FocusScopeIdentity: canvas.CanvasTransform.ElementIdentity,
                VirtualListIdentity: list.ElementIdentity, VirtualRowIdentity: row.ElementIdentity,
                VirtualRowLease: lease, VirtualProviderIncarnation: list.VirtualProviderIncarnation);
            UIPublication packet = new(1, canvas.PublicationLifetime, 1, 1,
                canvas.CanvasTransform.ChangeJournal.Revision, default,
                new UIElementIdentity[] { canvas.CanvasTransform.ElementIdentity,
                    list.ElementIdentity, row.ElementIdentity }, [hit], [], bounds,
                Matrix4x4.Identity, Matrix4x4.Identity);
            canvas.MarkPresented(canvas, packet);
            packet.Release();
            UIPublication? read = (UIPublication?)typeof(UICanvasInputComponent)
                .GetMethod("RefreshPresentedInput", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(input, null);
            read?.Release();
            Assert.That(input.FocusedComponent, Is.SameAs(button));
            Assert.That(admitted, Is.EqualTo(synchronize));
        }
        finally { root.Destroy(true); }
    }
}
