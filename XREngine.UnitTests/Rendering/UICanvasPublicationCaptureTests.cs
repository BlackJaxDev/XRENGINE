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

    private static void Capture(UICanvasComponent canvas)
        => typeof(UICanvasComponent).GetMethod("CapturePublicationCandidate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, null);

    private static UIPublication? Candidate(UICanvasComponent canvas)
        => (UIPublication?)typeof(UICanvasComponent).GetField("_candidatePublication",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas);
}
