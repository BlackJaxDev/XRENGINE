using System.Collections;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIBackdropPortableTests
{
    [Test]
    public void PublicationPresenceFlagsFollowRenderStorageAcrossMappingsAndVariants()
    {
        UIRenderRecord[] records = [Quad(1, new(true, 3.0f)),
            Quad(2, default) with { RenderPass = (int)EDefaultRenderPass.PreRender }];
        UIPublication source = new(1, 1, 1, 1, 1, default, default, [], records,
            new BoundingRectangleF(0, 0, 100, 100), Matrix4x4.Identity, Matrix4x4.Identity);
        UIPublication mapped = source.WithOutputMapping(Matrix4x4.Identity, Matrix4x4.Identity,
            ECanvasDrawSpace.Screen, new BoundingRectangleF(4, 5, 100, 100),
            Matrix4x4.Identity, Matrix4x4.Identity, 0, 1);
        UIPublication variant = source.WithOutputVariant([Quad(1, default)], 2,
            Matrix4x4.Identity, Matrix4x4.Identity,
            new BoundingRectangleF(0, 0, 100, 100));
        try
        {
            source.HasVisibleBackdrop.ShouldBeTrue();
            source.HasVisiblePreRenderDraw.ShouldBeTrue();
            mapped.HasVisibleBackdrop.ShouldBeTrue();
            mapped.HasVisiblePreRenderDraw.ShouldBeTrue();
            variant.HasVisibleBackdrop.ShouldBeFalse();
            variant.HasVisiblePreRenderDraw.ShouldBeFalse();
        }
        finally
        {
            variant.Release();
            mapped.Release();
            source.Release();
        }
    }

    [Test]
    public void CollectorRequiresEveryDistinctNativeConsumerAndKeepsFrozenRequirementOnUnbind()
    {
        using UITestShaderScope shaders = new();
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        using UIBatchCollector collector = new();
        RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?>
            { [pass] = null });
        UIPublication publication = new(1, 1, 1, 1, 1, default, default, [],
            [Quad(1, new(true, 3.0f)), Quad(2, new(true, 7.0f))],
            new BoundingRectangleF(0, 0, 100, 100), Matrix4x4.Identity,
            Matrix4x4.Identity);
        try
        {
            collector.CollectPublishedRuns(publication, commands, _ => Assert.Fail());
            collector.SwapBuffers();
            ulong[] runs = ReadRenderRunIds(collector, pass);
            runs.Length.ShouldBe(2);
            collector.BindRenderingPublication(publication);
            collector.BeginBackdropAuthoring();
            collector.IsBackdropAuthoringComplete(captureReady: true).ShouldBeFalse();
            collector.AcknowledgeBackdropConsumer(pass, runs[0]).ShouldBeTrue();
            collector.IsBackdropAuthoringComplete(captureReady: true).ShouldBeFalse();
            collector.AcknowledgeBackdropConsumer(pass, runs[1]).ShouldBeTrue();
            collector.IsBackdropAuthoringComplete(captureReady: true).ShouldBeTrue();
            collector.AcknowledgeBackdropConsumer(pass, runs[0]).ShouldBeFalse();
            collector.IsBackdropAuthoringComplete(captureReady: true).ShouldBeFalse();
            collector.BindRenderingPublication(null);
            collector.IsBackdropAuthoringComplete(captureReady: true).ShouldBeFalse();
            collector.Dispose();
            collector.IsBackdropAuthoringComplete(captureReady: true).ShouldBeFalse();
        }
        finally { publication.Release(); }
    }

    [Test]
    public void CollectorKeepsCopyOnlyWriterClaimUntilTheReceiptCanRetire()
    {
        using UIRenderSurfacePool pool = new(EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba, EPixelType.UnsignedByte, 4);
        pool.TryReserve(32, 32, out UIRenderSurface? writer, out string? failure)
            .ShouldBeTrue(failure);
        writer.ShouldNotBeNull();
        using UIBatchCollector collector = new();
        UIReceiptCompletionProbe pending = new(() => false);
        pending.Seal(authored: false, fence: null);
        var activeField = typeof(UIBatchCollector).GetField("_activeBackdropSurface",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pendingField = typeof(UIBatchCollector).GetField("_pendingBackdropSurfaces",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IList pendingList = (IList)pendingField.GetValue(collector)!;
        try
        {
            activeField.SetValue(collector, writer);
            typeof(UIBatchCollector).GetMethod("SealBackdropSurface",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(collector, [pending]);
            activeField.GetValue(collector).ShouldBeNull();
            pendingList.Count.ShouldBe(1);
            writer.IsPoolOnly.ShouldBeFalse();
            pool.TryReserve(32, 32, out UIRenderSurface? second,
                out string? secondFailure).ShouldBeTrue(secondFailure);
            second.ShouldNotBeSameAs(writer);
            second!.Release();
        }
        finally
        {
            pendingList.Clear();
            writer.Release();
        }
    }

    [Test]
    public void ConsumerStrengthPartitionsRunsWithoutAddingAProducerRecord()
    {
        using UITestShaderScope shaders = new();
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        using UIBatchCollector collector = new();
        RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?>
            { [pass] = null });
        UIPublication publication = new(1, 1, 1, 1, 1, default, default, [],
            [Quad(1, default), Quad(2, new(true, 3.0f)),
             Quad(3, new(true, 3.0f)), Quad(4, new(true, 7.0f))],
            new BoundingRectangleF(0, 0, 100, 100), Matrix4x4.Identity,
            Matrix4x4.Identity);
        try
        {
            collector.CollectPublishedRuns(publication, commands,
                _ => Assert.Fail("A backdrop request must remain an intrinsic batch draw."));
            commands.GetUpdatingPassCommandCount(pass).ShouldBe(3);
            publication.Renders.Length.ShouldBe(4);
        }
        finally { publication.Release(); }
    }

    [Test]
    public void IssuedCopyCannotPublishAnUnretiredProducerGeneration()
    {
        using UIRenderSurfacePool pool = new(EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba, EPixelType.UnsignedByte, 4);
        pool.TryReserve(32, 32, out UIRenderSurface? writer, out string? failure)
            .ShouldBeTrue(failure);
        writer.ShouldNotBeNull();
        UIReceiptCompletionProbe incomplete = new(() => false);
        incomplete.Seal(authored: true, fence: null);
        pool.TryPublish(writer, writer.ReservationGeneration, incomplete).ShouldBeFalse();
        writer.IsPoolOnly.ShouldBeFalse();
        writer.Release();
        writer.IsPoolOnly.ShouldBeTrue();
    }

    [Test]
    public void BackdropShaderUsesPhysicalOriginAndHasNoMissingCaptureFallback()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory,
            "Build", "CommonAssets", "Shaders", "Common", "UIBackdropBatched.fs")))
            directory = Directory.GetParent(directory)?.FullName;
        directory.ShouldNotBeNull();
        string source = File.ReadAllText(Path.Combine(directory!, "Build", "CommonAssets",
            "Shaders", "Common", "UIBackdropBatched.fs"));
        source.ShouldContain("XRENGINE_FramebufferUV(gl_FragCoord.xy, CaptureOrigin, CaptureSize)");
        source.ShouldContain("clamp(uv + TapOffsets[i] * stepUV, lower, upper)");
        source.ShouldContain("i < 12");
        source.ShouldNotContain("grabSize.x <= 1");
    }

    private static UIRenderRecord Quad(long id, UIBackdropRequest backdrop) => new()
    {
        RenderInfo = null!, MembershipId = id,
        Identity = new UIElementIdentity(id, 1),
        WorldMatrix = Matrix4x4.Identity, Bounds = new Vector4(0, 0, 16, 16),
        RenderPass = (int)EDefaultRenderPass.TransparentForward,
        ZIndex = 0, Material = null, Visible = true, SupportsBatch = true,
        QuadColor = Vector4.One, Backdrop = backdrop,
    };

    private static ulong[] ReadRenderRunIds(UIBatchCollector collector, int pass)
    {
        var pools = (IDictionary)typeof(UIBatchCollector).GetField("_renderMaterialGroups",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(collector)!;
        object pool = pools[pass]!;
        int count = (int)pool.GetType().GetField("Count")!.GetValue(pool)!;
        var items = (IList)pool.GetType().GetField("Items")!.GetValue(pool)!;
        ulong[] ids = new ulong[count];
        for (int index = 0; index < count; index++)
            ids[index] = (ulong)items[index]!.GetType().GetField("RunId")!.GetValue(items[index])!;
        return ids;
    }
}
