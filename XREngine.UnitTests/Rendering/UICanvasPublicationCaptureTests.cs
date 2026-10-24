using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
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

    private static void Capture(UICanvasComponent canvas)
        => typeof(UICanvasComponent).GetMethod("CapturePublicationCandidate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, null);

    private static UIPublication? Candidate(UICanvasComponent canvas)
        => (UIPublication?)typeof(UICanvasComponent).GetField("_candidatePublication",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas);
}
