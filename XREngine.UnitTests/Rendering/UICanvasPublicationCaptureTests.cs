using System.Reflection;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UICanvasPublicationCaptureTests
{
    private UITestShaderScope _shaderScope = null!;
    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown] public void RestoreShaderAssets() => _shaderScope.Dispose();

    [Test]
    public void PaintChangesKeepHitGenerationButEligibilityChangesReplaceIt()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Publication hit revisions", transform);
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            var button = root.NewChild("Button").AddComponent<UIVirtualButtonComponent>()!;
            button.BoundableTransform.Width = 20;
            button.BoundableTransform.Height = 20;
            transform.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => transform.PumpLayoutResults(), TimeSpan.FromSeconds(5))
                .ShouldBeTrue();
            Capture(canvas);
            UIPublication first = Candidate(canvas).ShouldNotBeNull();
            ulong hitGeneration = first.HitGeneration;
            first.Hits.Length.ShouldBe(1);

            button.IsSelected = true;
            Capture(canvas);
            UIPublication painted = Candidate(canvas).ShouldNotBeNull();
            painted.Generation.ShouldBeGreaterThan(first.Generation);
            painted.HitGeneration.ShouldBe(hitGeneration);

            button.IsEnabled = false;
            Capture(canvas);
            UIPublication disabled = Candidate(canvas).ShouldNotBeNull();
            disabled.HitGeneration.ShouldBeGreaterThan(hitGeneration);
            disabled.Hits.Length.ShouldBe(1);
            disabled.Hits[0].InteractionRevision.ShouldBe(button.InteractionRevision);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void CaptureMutationRejectsMixedCandidateAndCleanRetrySucceeds()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Publication mutation", transform);
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UIPublicationMutationProbe probe = root.NewChild("Probe")
                .AddComponent<UIPublicationMutationProbe>()!;
            probe.BoundableTransform.Width = 20;
            probe.BoundableTransform.Height = 20;
            transform.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => transform.PumpLayoutResults(), TimeSpan.FromSeconds(5))
                .ShouldBeTrue();

            Capture(canvas);
            UIPublication first = Candidate(canvas).ShouldNotBeNull();
            probe.CaptureCount.ShouldBeGreaterThan(0);

            probe.BoundableTransform.RecordUIChange(UIChangeDomain.Visual);
            probe.MutateDuringCapture = true;
            Capture(canvas);

            Candidate(canvas).ShouldBeSameAs(first);
            transform.ChangeJournal.Revision.ShouldBeGreaterThan(first.ChangeRevision);

            probe.MutateDuringCapture = false;
            Capture(canvas);
            UIPublication accepted = Candidate(canvas).ShouldNotBeNull();
            accepted.ShouldNotBeSameAs(first);
            accepted.ChangeRevision.ShouldBe(transform.ChangeJournal.Revision);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void CaptureInheritsCompositionOwnerAndNestedLayerOverridesIt()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Layer inheritance", transform);
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            root.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.StaticChrome;
            UIPublicationMutationProbe chrome = root.NewChild("Chrome").AddComponent<UIPublicationMutationProbe>()!;
            chrome.BoundableTransform.Width = 20;
            chrome.BoundableTransform.Height = 20;
            UIPublicationMutationProbe overlay = root.NewChild("Overlay").AddComponent<UIPublicationMutationProbe>()!;
            overlay.BoundableTransform.Width = 20;
            overlay.BoundableTransform.Height = 20;
            overlay.SceneNode.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.Overlay;
            transform.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => transform.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
            Capture(canvas);
            UIPublication publication = Candidate(canvas).ShouldNotBeNull();
            publication.Renders.Length.ShouldBe(2);
            foreach (ref readonly UIRenderRecord record in publication.Renders)
                record.CompositionOwner.ShouldBe(record.Identity == chrome.UITransform.ElementIdentity
                    ? new UICompositionOwner(transform.ElementIdentity, EUICompositionLayerKind.StaticChrome)
                    : new UICompositionOwner(overlay.UITransform.ElementIdentity, EUICompositionLayerKind.Overlay));
            publication.CompositionSegments.Length.ShouldBe(2);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NestedClipsStayFrozenAcrossBatchedAndFallbackPainterOrder(bool offscreen)
    {
        using IDisposable materialTarget = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
            EngineMaterialConstructionTarget.DesktopGlsl);
        UICanvasTransform canvasTransform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Clipped publication", canvasTransform) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
        SceneNode outerNode = new(root) { Name = "Outer" };
        UIBoundableTransform outer = outerNode.SetTransform<UIBoundableTransform>();
        outerNode.AddComponent<UIPublicationMutationProbe>()!.ClipToBounds = true;
        SceneNode innerNode = new(outerNode) { Name = "Inner" };
        UIBoundableTransform inner = innerNode.SetTransform<UIBoundableTransform>();
        innerNode.AddComponent<UIPublicationMutationProbe>()!.ClipToBounds = true;
        UIMaterialComponent first = Quad(innerNode, "First batch", 1);
        UIMaterialComponent fallback = Quad(innerNode, "Fallback", 2);
        UIMaterialComponent third = Quad(innerNode, "Third batch", 3);
        UIMaterialComponent outerLast = Quad(outerNode, "Outer batch", 4);
        XRMaterial? fallbackMaterial = null;
        UIPublication? publication = null;
        try
        {
            if (offscreen)
            {
                canvasTransform.DrawSpace = ECanvasDrawSpace.World;
                canvas.PreferOffscreenRenderingForNonScreenSpaces = true;
            }
            root.IsActiveSelf = true;
            foreach (UIMaterialComponent component in new[] { first, fallback, third, outerLast })
                component.SetBatchSolidColor(Vector4.One);
            // The repository's default legacy shader uses host-file snippets. A self-contained
            // custom material exercises the actual frozen fallback draw in headless tests.
            fallbackMaterial = new XRMaterial(Array.Empty<ShaderVar>(), new XRShader(EShaderType.Fragment,
                "#version 450\nlayout(location=0) out vec4 FragColor;\nvoid main() { FragColor = vec4(1.0); }"));
            fallback.Material = fallbackMaterial;
            fallback.DisableBatching = true;
            Apply(canvasTransform, new Vector2(100), Vector2.Zero);
            Apply(outer, new Vector2(80), new Vector2(10));
            Apply(inner, new Vector2(40), new Vector2(20));
            foreach (UIMaterialComponent component in new[] { first, fallback, third, outerLast })
                Apply(component.BoundableTransform, new Vector2(24), Vector2.Zero);
            canvasTransform.RecalculateMatrixHierarchyImmediate();
            outer.RefreshAxisAlignedRegion();
            inner.RefreshAxisAlignedRegion();

            UIRenderRecord[] records = new[] { first, fallback, third, outerLast }
                .Select(component => component.CapturePublicationRenderRecord(new UILayoutNodeOutput
                {
                    ActualSize = new Vector2(24), Visible = true
                })).ToArray();
            Assert.That(records.All(record => record.Visible && record.CaptureFailure is null), Is.True,
                string.Join("; ", records.Select((record, index) =>
                    $"{index}: visible={record.Visible}, active={new[] { first, fallback, third, outerLast }[index].IsActiveInHierarchy}, renderInfo={record.RenderInfo.IsVisible}, " +
                    $"batch={record.SupportsBatch}, failure={record.CaptureFailure ?? "<none>"}")));
            records[0].SupportsBatch.ShouldBeTrue();
            records[1].SupportsBatch.ShouldBeFalse();
            records[1].FrozenCommand.ShouldNotBeNull();
            records[2].SupportsBatch.ShouldBeTrue();
            records[3].SupportsBatch.ShouldBeTrue();
            BoundingRectangle? resolvedInner = UIClipRegion.ResolveCrop(first.BoundableTransform, false);
            BoundingRectangle? resolvedOuter = UIClipRegion.ResolveCrop(outerLast.BoundableTransform, false);
            resolvedInner.ShouldNotBeNull();
            resolvedOuter.ShouldNotBeNull();
            BoundingRectangle innerCrop = resolvedInner.Value;
            BoundingRectangle outerCrop = resolvedOuter.Value;
            innerCrop.ShouldNotBe(outerCrop);
            for (int index = 0; index < 3; index++) records[index].Crop.ShouldBe(innerCrop);
            records[3].Crop.ShouldBe(outerCrop);
            records[1].FrozenCommand!.WorldCropRegion.ShouldBe(innerCrop);
            records[1].FrozenCommand!.UICanvasCropSpace.ShouldBe(canvasTransform.GetActualBounds());

            publication = new UIPublication(1, 1, 1, 1, 1,
                ReadOnlyMemory<UILayoutNodeOutput>.Empty, ReadOnlyMemory<UIElementIdentity>.Empty,
                [], records, canvasTransform.GetActualBounds(), Matrix4x4.Identity, Matrix4x4.Identity,
                authoringDrawSpace: offscreen ? ECanvasDrawSpace.World : ECanvasDrawSpace.Screen,
                authoringOffscreen: offscreen);
            Apply(inner, new Vector2(40), new Vector2(25));
            canvasTransform.RecalculateMatrixHierarchyImmediate();
            inner.RefreshAxisAlignedRegion();
            UIClipRegion.ResolveCrop(first.BoundableTransform, false).ShouldNotBe(innerCrop);
            publication.Renders[0].Crop.ShouldBe(innerCrop);
            publication.Renders[1].FrozenCommand!.WorldCropRegion.ShouldBe(innerCrop);
            const int pass = (int)EDefaultRenderPass.TransparentForward;
            RenderCommandCollection commands = new(new Dictionary<int, IComparer<RenderCommand>?>
            {
                [pass] = new NearToFarRenderCommandSorter()
            });
            using UIBatchCollector collector = new();
            List<UIElementIdentity> fallbackOrder = [];
            collector.CollectPublishedRuns(publication, commands, record =>
            {
                fallbackOrder.Add(record.Identity);
                commands.AddCPU(record.FrozenCommand!);
            });
            fallbackOrder.ShouldBe([fallback.UITransform.ElementIdentity]);
            commands.GetUpdatingPassCommandCount(pass).ShouldBe(4);
            var updating = (Dictionary<int, ICollection<RenderCommand>>)typeof(RenderCommandCollection)
                .GetField("_updatingPasses", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(commands)!;
            updating[pass].Cast<RenderCommand2D>().Select(command => command.ZIndex)
                .ShouldBe([1, 2, 3, 4]);
            UIBatchCollector.MapCanvasCropToRaster(innerCrop, canvasTransform.GetActualBounds(),
                    new BoundingRectangle(200, 300, 200, 200))
                .ShouldNotBe(UIBatchCollector.MapCanvasCropToRaster(outerCrop,
                    canvasTransform.GetActualBounds(), new BoundingRectangle(200, 300, 200, 200)));
        }
        finally
        {
            publication?.Release();
            root.Destroy(true);
            fallbackMaterial?.Destroy(true);
        }
    }

    private static UIMaterialComponent Quad(SceneNode parent, string name, int zIndex)
    {
        SceneNode node = new(parent) { Name = name };
        node.SetTransform<UIBoundableTransform>();
        UIMaterialComponent component = node.AddComponent<UIMaterialComponent>()!;
        component.RenderCommand2D.ZIndex = zIndex;
        return component;
    }

    private static void Apply(UIBoundableTransform transform, Vector2 size, Vector2 translation)
    {
        transform.ApplyCalculatedLayout(new UILayoutNodeOutput
        {
            ActualSize = size, LocalTranslation = translation, Visible = true
        }, Vector2.Zero);
        transform.RefreshAxisAlignedRegion();
    }

    private static void Capture(UICanvasComponent canvas)
        => typeof(UICanvasComponent).GetMethod("CapturePublicationCandidate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, null);

    private static UIPublication? Candidate(UICanvasComponent canvas)
        => (UIPublication?)typeof(UICanvasComponent).GetField("_candidatePublication",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas);
}
