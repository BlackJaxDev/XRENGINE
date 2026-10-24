using System;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIFrozenWorldCollectionTests
{
    [Test]
    public void IndexedIntentCapturesTheOwnedMeshStreamAndRejectsMissingTopology()
    {
        XRMesh mesh = XRMesh.Create(VertexQuad.PosZ(1, true, 0, false));
        XRMeshRenderer renderer = new(mesh, null);
        try
        {
            UIIndexedDrawIntent intent = UIIndexedDrawIntent.Capture(
                renderer, EPrimitiveType.Triangles, instances: 2);

            intent.Renderer.ShouldBeSameAs(renderer);
            intent.GeometryRevision.ShouldBe(mesh.GeometryRevision);
            intent.IndexCount.ShouldBe(checked((uint)(mesh.Triangles!.Count * 3)));
            intent.ElementType.ShouldBe(IndexSize.TwoBytes);
            intent.Instances.ShouldBe(2u);
            intent.IndexOffset.ShouldBe((nint)0);
            Should.Throw<InvalidOperationException>(() => UIIndexedDrawIntent.Capture(
                renderer, EPrimitiveType.Lines, instances: 2));
        }
        finally
        {
            renderer.Destroy(true);
            mesh.Destroy(true);
        }
    }

    [Test]
    public void FrozenChildCullingUsesTheActualCollectionVolume()
    {
        AABB view = new(new Vector3(-1), new Vector3(1));
        Box inside = new AABB(new Vector3(-0.5f), new Vector3(0.5f))
            .ToBox(Matrix4x4.Identity);
        Box outside = new AABB(new Vector3(2), new Vector3(3))
            .ToBox(Matrix4x4.Identity);

        RenderInfo3D.FrozenWorldVolumeIntersects(view, inside, containsOnly: false)
            .ShouldBeTrue();
        RenderInfo3D.FrozenWorldVolumeIntersects(view, inside, containsOnly: true)
            .ShouldBeTrue();
        RenderInfo3D.FrozenWorldVolumeIntersects(view, outside, containsOnly: false)
            .ShouldBeFalse();
    }

    [Test]
    public void CollectPacketSetRetainsExactPacketsAcrossReadersAndRejectsChangedIdentity()
    {
        UIPublication first = NewPacket(1);
        UIPublication changed = NewPacket(2);
        object canvas = new();
        var set = new UIWorldCollectPacketSet();
        set.TryBegin().ShouldBeTrue();
        set.TryAdd(canvas, first).ShouldBeTrue();
        changed.Retain();
        set.TryAdd(canvas, changed).ShouldBeFalse();
        set.TrySeal().ShouldBeFalse();
        set.Release();
        Should.Throw<ObjectDisposedException>(first.Retain);
        changed.Release();
        Should.Throw<ObjectDisposedException>(changed.Retain);

        UIPublication selected = NewPacket(3);
        set.TryBegin().ShouldBeTrue();
        set.TryAdd(canvas, selected).ShouldBeTrue();
        set.TrySeal().ShouldBeTrue();
        set.TryRetain().ShouldBeTrue();
        set.Release();
        set.TryGet(canvas, out UIPublication? held).ShouldBeTrue();
        held.ShouldBeSameAs(selected);
        set.TryBegin().ShouldBeFalse();
        set.Release();
        Should.Throw<ObjectDisposedException>(selected.Retain);
    }

    [Test]
    public void CollectPacketSetCannotReuseItsSlotDuringFinalRetirement()
    {
        UIPublication selected = NewPacket(4);
        var set = new UIWorldCollectPacketSet();
        set.TryBegin().ShouldBeTrue();
        set.TryAdd(new object(), selected).ShouldBeTrue();
        set.TrySeal().ShouldBeTrue();
        set.ReleaseForTesting(() =>
        {
            set.TryBegin().ShouldBeFalse();
            set.TryRetain().ShouldBeFalse();
        });
        Should.Throw<ObjectDisposedException>(selected.Retain);
        set.TryBegin().ShouldBeTrue();
        set.TrySeal().ShouldBeTrue();
        set.Release();
    }

    [Test]
    public void MissingCpuPassReportsRejectedInsertion()
    {
        var commands = new RenderCommandCollection(
            new System.Collections.Generic.Dictionary<int, System.Collections.Generic.IComparer<RenderCommand>?>
            {
                [1] = null
            });
        commands.TryAddCPU(new RenderCommandMesh3D(2), null).ShouldBeFalse();
        commands.GetUpdatingCommandCount().ShouldBe(0);
    }

    [Test]
    public void ReusedPacketSlotRejectsTheOldCollectGeneration()
    {
        var set = new UIWorldCollectPacketSet();
        set.TryBegin(10).ShouldBeTrue();
        set.TrySeal().ShouldBeTrue();
        set.Release();
        set.TryBegin(11).ShouldBeTrue();
        set.TrySeal().ShouldBeTrue();
        set.TryRetain(10).ShouldBeFalse();
        set.TryRetain(11).ShouldBeTrue();
        set.Release();
        set.Release();
    }

    [Test]
    public void CollectPacketSetRejectsMoreThanSixtyFourCanvasPackets()
    {
        var set = new UIWorldCollectPacketSet();
        set.TryBegin().ShouldBeTrue();
        for (int index = 0; index < UIWorldCollectPacketSet.MaximumPackets; index++)
            set.TryAdd(new object(), NewPacket((ulong)index + 1UL)).ShouldBeTrue();
        UIPublication overflow = NewPacket(100);
        set.TryAdd(new object(), overflow).ShouldBeFalse();
        Should.Throw<ObjectDisposedException>(overflow.Retain);
        set.Failed.ShouldBeTrue();
        set.TrySeal().ShouldBeFalse();
        set.Release();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CollectCallbackCannotPublishAfterWorldCloseOrDispose(bool dispose)
    {
        using var shaders = new UITestShaderScope();
        var scene = new VisualScene3D();
        RuntimeWorldRenderer world = new(new TestWorldContext(), scene);
        var info = RenderInfo3D.New(new FrozenTestRenderable());
        UIPublication packet = NewPacket(8);
        info.FrozenWorldCommandSource = new ClosingPacketSource(packet, () =>
        {
            if (dispose)
                world.Dispose();
            else
                world.SetCollectPublicationOpen(false);
        });
        scene.AddRenderable(info);
        world.SetCollectPublicationOpen(true);
        try
        {
            world.GlobalPreCollectVisible();
            scene.FrozenWorldPacketSetForCollection.ShouldBeNull();
            world.TryRetainRenderingFrozenWorldPacketSet(out _).ShouldBeFalse();
            if (dispose)
                Should.Throw<ObjectDisposedException>(() => world.SetCollectPublicationOpen(true));
        }
        finally
        {
            world.Dispose();
            info.Dispose();
            packet.Release();
        }
        Should.Throw<ObjectDisposedException>(packet.Retain);
    }

    [Test]
    public void SceneTraversalKeepsItsSelectedPacketWhenACollectionCallbackClearsTheScene()
    {
        using var shaders = new UITestShaderScope();
        var scene = new VisualScene3D();
        scene.ApplyRenderDispatchPreference(false);
        var source = new ObservingPacketSource();
        var info = RenderInfo3D.New(new FrozenTestRenderable());
        info.FrozenWorldCommandSource = source;
        scene.AddRenderable(info);
        scene.GlobalCollectVisible();

        UIPublication packet = NewPacket(9);
        var set = new UIWorldCollectPacketSet();
        set.TryBegin(9).ShouldBeTrue();
        set.TryAdd(source.PacketKey, packet).ShouldBeTrue();
        set.TrySeal().ShouldBeTrue();
        scene.SetFrozenWorldPacketSetForCollection(set);
        bool cleared = false;
        info.CullingIntersectionOverride = (_, _, _) =>
        {
            if (!cleared)
            {
                cleared = true;
                scene.SetFrozenWorldPacketSetForCollection(null);
                set.Release();
            }
            return true;
        };
        var commands = new RenderCommandCollection();
        try
        {
            scene.CollectRenderedItems(commands, null, null, collectMirrors: false);
            cleared.ShouldBeTrue();
            source.Observed.ShouldBeSameAs(packet);
            source.WasRetiredDuringCollection.ShouldBeFalse();
            Should.Throw<ObjectDisposedException>(packet.Retain);
        }
        finally
        {
            if (!cleared)
            {
                scene.SetFrozenWorldPacketSetForCollection(null);
                set.Release();
            }
            info.Dispose();
        }
    }

    private sealed class ObservingPacketSource : IFrozenWorldCommandSource,
        IFrozenWorldPacketSource
    {
        public object PacketKey { get; } = new();
        public UIPublication? Observed { get; private set; }
        public bool WasRetiredDuringCollection { get; private set; }
        public bool IsInWorld(IRuntimeWorldContext expectedWorld) => true;
        public bool TryAcquireCollectPacket(IRuntimeWorldContext expectedWorld,
            out UIPublication? selected)
        {
            selected = null;
            return true;
        }
        public bool HasFrozenShadowCasters(UIWorldCollectPacketSet? packetSet) => false;
        public ERenderInfoCollectResult CollectFrozenCommands(
            RenderCommandCollection commands, IRuntimeCullingCamera? camera,
            IVolume? collectionVolume, bool containsOnly,
            UIWorldCollectPacketSet? packetSet)
        {
            packetSet!.TryGet(PacketKey, out UIPublication? selected).ShouldBeTrue();
            Observed = selected;
            WasRetiredDuringCollection = selected!.ResourcesRetired;
            return ERenderInfoCollectResult.Collected;
        }
    }

    private sealed class FrozenTestRenderable : IRenderable
    {
        public RenderInfo[] RenderedObjects => [];
        public float TransformDepth => 0.0f;
    }

    private sealed class ClosingPacketSource(UIPublication packet, Action close) : IFrozenWorldCommandSource,
        IFrozenWorldPacketSource
    {
        public object PacketKey { get; } = new();
        public bool IsInWorld(IRuntimeWorldContext expectedWorld) => true;
        public bool TryAcquireCollectPacket(IRuntimeWorldContext expectedWorld,
            out UIPublication? selected)
        {
            packet.Retain();
            selected = packet;
            close();
            return true;
        }
        public bool HasFrozenShadowCasters(UIWorldCollectPacketSet? packetSet) => false;
        public ERenderInfoCollectResult CollectFrozenCommands(
            RenderCommandCollection commands, IRuntimeCullingCamera? camera,
            IVolume? collectionVolume, bool containsOnly,
            UIWorldCollectPacketSet? packetSet)
            => ERenderInfoCollectResult.RejectedForEveryCamera;
    }

    private static UIPublication NewPacket(ulong generation)
        => new(generation, 1, 1, 1, (long)generation, default, default, [], [],
            new BoundingRectangleF(Vector2.Zero, Vector2.One),
            Matrix4x4.Identity, Matrix4x4.Identity);
}
