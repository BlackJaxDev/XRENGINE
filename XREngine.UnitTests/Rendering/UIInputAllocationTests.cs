using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

/// <summary>Checks warmed input collection without a window or graphics device.</summary>
[TestFixture, NonParallelizable]
public sealed class UIInputAllocationTests
{
    [Test]
    public void IdleQueriesReuseHitsAndRejectChangedOutputOrHitGeneration()
    {
        using var shaderScope = new UITestShaderScope();
        var root = new SceneNode("Input cache", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        var packets = new List<UIPublication>();
        bool countersEnabled = UIRuntimeCounters.Enabled;
        try
        {
            var canvas = root.AddComponent<UICanvasComponent>()!;
            var input = root.AddComponent<UICanvasInputComponent>()!;
            var button = root.NewChild("Button").AddComponent<UIVirtualButtonComponent>()!;
            var bounds = new BoundingRectangleF(Vector2.Zero, new Vector2(100));
            UIHitRecord hit = new(button.RenderInfo2D, button.RenderInfo2D.MembershipId,
                button.UITransform.ElementIdentity, button.InteractionRevision,
                Matrix4x4.Identity, new Vector2(20), bounds, null,
                1, 0, 2, 1, 0, 0, true, false);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var move = typeof(UICanvasInputComponent).GetMethod("MouseMove", flags)!
                .CreateDelegate<Action<float, float>>(input);
            var collect = typeof(UICanvasInputComponent).GetMethod("CollectVisible", flags)!
                .CreateDelegate<Action>(input);
            var swap = typeof(UICanvasInputComponent).GetMethod("SwapBuffers", flags)!
                .CreateDelegate<Action>(input);
            UIRuntimeCounters.Enabled = true;
            Publish(1, 1, Matrix4x4.Identity, hit);
            move(10, 10);
            Query();
            long queries = UIRuntimeCounters.Read(UIWorkCounter.HitQueries);
            for (int index = 0; index < 20; index++)
                Query();
            Assert.That(UIRuntimeCounters.Read(UIWorkCounter.HitQueries), Is.EqualTo(queries));
            Assert.That(input.TopMostInteractable, Is.SameAs(button));

            // A new paint packet preserves the hit generation and its cached query.
            Publish(2, 1, Matrix4x4.Identity, hit);
            Query();
            Assert.That(UIRuntimeCounters.Read(UIWorkCounter.HitQueries), Is.EqualTo(queries));

            Publish(3, 1, Matrix4x4.CreateTranslation(30, 0, 0), hit);
            Query();
            Assert.That(input.TopMostInteractable, Is.Null);
            Assert.That(UIRuntimeCounters.Read(UIWorkCounter.HitQueries), Is.EqualTo(++queries));

            Publish(4, 2, Matrix4x4.CreateTranslation(30, 0, 0), hit with { Size = new Vector2(100) });
            Query();
            Assert.That(input.TopMostInteractable, Is.SameAs(button));
            Assert.That(UIRuntimeCounters.Read(UIWorkCounter.HitQueries), Is.EqualTo(++queries));

            void Publish(ulong generation, ulong hitGeneration, Matrix4x4 outputToCanvas, UIHitRecord record)
            {
                Matrix4x4.Invert(outputToCanvas, out Matrix4x4 canvasToOutput);
                var packet = new UIPublication(generation, 1, 1, hitGeneration, (long)generation,
                    default, default, [record], [], bounds, canvasToOutput, outputToCanvas);
                packets.Add(packet);
                canvas.MarkPresented(canvas, packet);
            }
            void Query() { collect(); swap(); }
        }
        finally
        {
            UIRuntimeCounters.Enabled = countersEnabled;
            root.Destroy(true);
            foreach (UIPublication packet in packets)
                packet.Release();
        }
    }

    [Test]
    public void WarmBlockerQueriesKeepOnlyTheFrozenSubtreeWithoutAllocating()
    {
        using var shaderScope = new UITestShaderScope();
        var root = new SceneNode("Input blocker", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        UIPublication? packet = null;
        try
        {
            var canvas = root.AddComponent<UICanvasComponent>()!;
            var input = root.AddComponent<UICanvasInputComponent>()!;
            var behind = root.NewChild("Behind").AddComponent<UIVirtualButtonComponent>()!;
            var blockerNode = root.NewChild("Blocker");
            var blocker = blockerNode.AddComponent<UIVirtualButtonComponent>()!;
            var child = blockerNode.NewChild("Child").AddComponent<UIVirtualButtonComponent>()!;
            var bounds = new BoundingRectangleF(Vector2.Zero, new Vector2(100));
            UIHitRecord[] hits = [Make(behind, 1, 2, 1, false),
                Make(blocker, 2, 4, 1, true), Make(child, 3, 4, 2, false)];
            packet = new UIPublication(1, 1, 1, 1, 1, default, default, hits, [],
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
                Query(index);
            long allocated;
            using (RuntimeRenderingHostServices.Install(new UninstalledRuntimeRenderingHostServices()))
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int index = 0; index < 1000; index++)
                    Query(index);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Assert.Multiple(() =>
            {
                Assert.That(allocated, Is.Zero);
                Assert.That(behind.IsMouseOver, Is.False);
                Assert.That(blocker.IsMouseOver, Is.True);
                Assert.That(child.IsMouseOver, Is.True);
                Assert.That(input.TopMostInteractable, Is.SameAs(child));
            });
            UIHitRecord Make(UIVirtualButtonComponent target, int node, int end, int depth, bool blocks)
                => new(target.RenderInfo2D, target.RenderInfo2D.MembershipId,
                    target.UITransform.ElementIdentity, target.InteractionRevision,
                    Matrix4x4.Identity, new Vector2(100), bounds, null,
                    node, 0, end, depth, 0, node, true, blocks);
            void Query(int index) { move(10 + (index & 1), 10); collect(); swap(); }
        }
        finally
        {
            root.Destroy(true);
            packet?.Release();
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void WarmPointerQueriesReuseTheirStorage(int movement)
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
            int entered = 0;
            int left = 0;
            button.MouseOverlapEnter += _ => entered++;
            button.MouseOverlapLeave += _ => left++;
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
                move(movement == 2 && (index & 1) != 0 ? 110 : 10, 10);
                collect();
                swap();
            }
            long allocated;
            int enteredBefore = entered;
            int leftBefore = left;
            // The lifecycle fixture uses DispatchProxy. Its argument array would count
            // as a query allocation even though the forwarded profiler returns null.
            using (RuntimeRenderingHostServices.Install(new UninstalledRuntimeRenderingHostServices()))
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int index = 0; index < 1000; index++)
                {
                    move(movement == 2 && (index & 1) != 0 ? 110 :
                        movement == 1 ? 10 + (index & 1) : 10, 10);
                    collect();
                    swap();
                }
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Assert.Multiple(() =>
            {
                if (movement == 2)
                    Assert.That(input.TopMostInteractable, Is.Null);
                else
                    Assert.That(input.TopMostInteractable, Is.SameAs(button));
                Assert.That(allocated, Is.Zero);
                Assert.That(entered - enteredBefore, Is.EqualTo(movement == 2 ? 500 : 0));
                Assert.That(left - leftBefore, Is.EqualTo(movement == 2 ? 500 : 0));
            });
        }
        finally
        {
            root.Destroy(true);
            packet?.Release();
        }
    }
}
