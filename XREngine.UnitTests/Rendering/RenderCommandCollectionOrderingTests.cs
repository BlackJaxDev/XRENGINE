using NUnit.Framework;
using Shouldly;
using System.Collections.Generic;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderCommandCollectionOrderingTests
{
    [Test]
    public void Equal2DCommands_PreserveSubmissionOrder_InTransparentPass()
    {
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?>
        {
            [pass] = new FarToNearRenderCommandSorter()
        });
        List<string> rendered = [];

        commands.AddCPU(new TestRenderCommand(pass, 0, "first", rendered));
        commands.AddCPU(new TestRenderCommand(pass, 0, "second", rendered));
        commands.AddCPU(new TestRenderCommand(pass, 0, "third", rendered));

        commands.SwapBuffers();
        commands.RenderCPU(pass);

        rendered.ShouldBe(["first", "second", "third"]);
    }

    [Test]
    public void Equal2DCommands_PreserveSubmissionOrder_InOpaquePass()
    {
        const int pass = (int)EDefaultRenderPass.OpaqueForward;
        RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?>
        {
            [pass] = new NearToFarRenderCommandSorter()
        });
        List<string> rendered = [];

        commands.AddCPU(new TestRenderCommand(pass, 0, "first", rendered));
        commands.AddCPU(new TestRenderCommand(pass, 0, "second", rendered));
        commands.AddCPU(new TestRenderCommand(pass, 0, "third", rendered));

        commands.SwapBuffers();
        commands.RenderCPU(pass);

        rendered.ShouldBe(["first", "second", "third"]);
    }

    [Test]
    public void VisualScene2D_NoCullingVolume_PreservesRenderableInsertionOrder()
    {
        const int pass = (int)EDefaultRenderPass.TransparentForward;

        VisualScene2D scene = new();
        scene.SetBounds(new BoundingRectangleF(0.0f, 0.0f, 100.0f, 100.0f));

        RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?>
        {
            [pass] = new FarToNearRenderCommandSorter()
        });
        List<string> rendered = [];

        TestRenderable topRight = new("top-right", pass, rendered, new BoundingRectangleF(75.0f, 75.0f, 5.0f, 5.0f));
        TestRenderable bottomLeft = new("bottom-left", pass, rendered, new BoundingRectangleF(5.0f, 5.0f, 5.0f, 5.0f));
        TestRenderable topLeft = new("top-left", pass, rendered, new BoundingRectangleF(5.0f, 75.0f, 5.0f, 5.0f));

        scene.AddRenderable(topRight.Info);
        scene.AddRenderable(bottomLeft.Info);
        scene.AddRenderable(topLeft.Info);

        scene.CollectRenderedItems(commands, (BoundingRectangleF?)null, null);
        commands.SwapBuffers();
        commands.RenderCPU(pass);

        rendered.ShouldBe(["top-right", "bottom-left", "top-left"]);
    }

    private sealed class TestRenderCommand(int renderPass, int zIndex, string name, List<string> rendered)
        : RenderCommand2D(renderPass, zIndex)
    {
        public override void Render() => rendered.Add(name);
    }

    private sealed class TestRenderable : IRenderable
    {
        public TestRenderable(string name, int renderPass, List<string> rendered, BoundingRectangleF bounds)
        {
            Info = RenderInfo2D.New(this, new TestRenderCommand(renderPass, 0, name, rendered));
            Info.CullingVolume = bounds;
            RenderedObjects = [Info];
        }

        public RenderInfo2D Info { get; }
        public RenderInfo[] RenderedObjects { get; }
    }
}