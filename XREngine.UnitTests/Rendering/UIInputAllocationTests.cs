using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

/// <summary>Checks warmed input collection without a window or graphics device.</summary>
[TestFixture, NonParallelizable]
public sealed class UIInputAllocationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void WarmPointerQueriesReuseTheirStorage(bool movePointer)
    {
        using var shaderScope = new UITestShaderScope();
        var root = new SceneNode("Input allocation", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        UIPublication? packet = null;
        try
        {
            var canvas = root.AddComponent<UICanvasComponent>()!;
            var input = root.AddComponent<UICanvasInputComponent>()!;
            var button = root.NewChild("Button").AddComponent<UIVirtualButtonComponent>()!;
            var bounds = new BoundingRectangleF(Vector2.Zero, new Vector2(100));
            UIHitRecord hit = new(button.RenderInfo2D, button.RenderInfo2D.MembershipId,
                button.UITransform.ElementIdentity, button.InteractionRevision,
                Matrix4x4.Identity, new Vector2(100), bounds, null,
                1, 0, 2, 1, 0, 0, true, false);
            packet = new UIPublication(1, 1, 1, 1, 1, default, default, [hit], [],
                bounds, Matrix4x4.Identity, Matrix4x4.Identity);
            canvas.MarkPresented(canvas, packet);

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var move = typeof(UICanvasInputComponent).GetMethod("MouseMove", flags)!
                .CreateDelegate<Action<float, float>>(input);
            var collect = typeof(UICanvasInputComponent).GetMethod("CollectVisible", flags)!
                .CreateDelegate<Action>(input);
            var swap = typeof(UICanvasInputComponent).GetMethod("SwapBuffers", flags)!
                .CreateDelegate<Action>(input);
            for (int index = 0; index < 100; index++)
            {
                move(10 + (index & 1), 10);
                collect();
                swap();
            }
            long allocated;
            // The lifecycle fixture uses DispatchProxy. Its argument array would count
            // as a query allocation even though the forwarded profiler returns null.
            using (RuntimeRenderingHostServices.Install(new UninstalledRuntimeRenderingHostServices()))
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int index = 0; index < 1000; index++)
                {
                    move(movePointer ? 10 + (index & 1) : 10, 10);
                    collect();
                    swap();
                }
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Assert.Multiple(() =>
            {
                Assert.That(input.TopMostInteractable, Is.SameAs(button));
                Assert.That(allocated, Is.Zero);
            });
        }
        finally
        {
            root.Destroy(true);
            packet?.Release();
        }
    }
}
