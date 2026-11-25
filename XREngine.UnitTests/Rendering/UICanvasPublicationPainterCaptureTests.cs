using System.Reflection;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UICanvasPublicationPainterCaptureTests
{
    private UITestShaderScope _shaderScope = null!;

    [SetUp]
    public void SetUp() => _shaderScope = new UITestShaderScope();

    [TearDown]
    public void TearDown() => _shaderScope.Dispose();

    [Test]
    public void RejectedCaptureKeepsAcceptedPainterSnapshot()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Painter capture", transform);
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UIPublicationMutationProbe probe = root.NewChild("Probe")
                .AddComponent<UIPublicationMutationProbe>()!;
            probe.BoundableTransform.Width = 20;
            probe.BoundableTransform.Height = 20;
            SettleLayout(transform);

            Capture(canvas);
            UIPublication first = Candidate(canvas).ShouldNotBeNull();
            ulong acceptedRevision = PainterRevision(canvas);
            long[] acceptedOrder = PainterOrder(canvas).ToArray();
            acceptedOrder.ShouldContain(probe.RenderInfo2D.MembershipId);

            canvas.VisualScene2D.RemoveRenderable(probe.RenderInfo2D);
            probe.MutateDuringCapture = true;
            Capture(canvas);

            Candidate(canvas).ShouldBeSameAs(first);
            PainterRevision(canvas).ShouldBe(acceptedRevision);
            PainterOrder(canvas).SequenceEqual(acceptedOrder).ShouldBeTrue();

            probe.MutateDuringCapture = false;
            Capture(canvas);
            UIPublication accepted = Candidate(canvas).ShouldNotBeNull();
            accepted.ShouldNotBeSameAs(first);
            accepted.Renders.Length.ShouldBe(0);
            PainterOrder(canvas).SequenceEqual(acceptedOrder.Where(
                membership => membership != probe.RenderInfo2D.MembershipId)).ShouldBeTrue();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ReentrantCaptureRejectsOuterCandidate()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Reentrant capture", transform);
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UIPublicationReentryProbe probe = root.NewChild("Probe")
                .AddComponent<UIPublicationReentryProbe>()!;
            probe.BoundableTransform.Width = 20;
            probe.BoundableTransform.Height = 20;
            SettleLayout(transform);

            Capture(canvas);
            UIPublication first = Candidate(canvas).ShouldNotBeNull();
            ulong acceptedRevision = PainterRevision(canvas);

            probe.BoundableTransform.RecordUIChange(UIChangeDomain.Visual);
            probe.OnCapture = () => Capture(canvas);
            Capture(canvas);

            Candidate(canvas).ShouldBeSameAs(first);
            PainterRevision(canvas).ShouldBe(acceptedRevision);
            probe.CaptureCount.ShouldBe(2);

            probe.OnCapture = null;
            Capture(canvas);
            Candidate(canvas).ShouldNotBeSameAs(first);
            probe.CaptureCount.ShouldBe(3);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ClearingDuringCollectionKeepsPacketUntilCallbackReturns()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Collection claim", transform);
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UIPublicationReentryProbe probe = root.NewChild("Probe")
                .AddComponent<UIPublicationReentryProbe>()!;
            UIRenderRecord record = new()
            {
                RenderInfo = probe.RenderInfo2D,
                MembershipId = probe.RenderInfo2D.MembershipId,
                Identity = probe.BoundableTransform.ElementIdentity,
                WorldMatrix = Matrix4x4.Identity,
                Bounds = Vector4.One,
                RenderPass = 0,
                ZIndex = 0,
                Material = null,
                Visible = true,
            };
            UIPublication packet = new(1, canvas.PublicationLifetime, 1, 1, 1,
                ReadOnlyMemory<UILayoutNodeOutput>.Empty,
                ReadOnlyMemory<UIElementIdentity>.Empty, [], [record],
                transform.GetActualBounds(), Matrix4x4.Identity, Matrix4x4.Identity);
            typeof(UICanvasComponent).GetField("_candidatePublication",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(canvas, packet);
            Invoke(canvas, "BeginPublicationCollection");

            bool callbackRan = false;
            typeof(UICanvasComponent).GetField("_collectPublishedUnbatched",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(canvas,
                (Action<UIRenderRecord>)(_ =>
                {
                    callbackRan = true;
                    Invoke(canvas, "ClearPublications");
                    packet.ResourcesRetired.ShouldBeFalse();
                    ((bool)Invoke(canvas, "CanCommitCollectedPublication")!).ShouldBeFalse();
                }));

            Invoke(canvas, "CollectPublishedCanvasDraws");

            callbackRan.ShouldBeTrue();
            packet.ResourcesRetired.ShouldBeTrue();
            Candidate(canvas).ShouldBeNull();
        }
        finally { root.Destroy(true); }
    }

    private static void SettleLayout(UICanvasTransform transform)
    {
        transform.UpdateLayoutAsync();
        SpinWait.SpinUntil(() => transform.PumpLayoutResults(), TimeSpan.FromSeconds(5))
            .ShouldBeTrue();
    }

    private static void Capture(UICanvasComponent canvas)
        => Invoke(canvas, "CapturePublicationCandidate");

    private static object? Invoke(UICanvasComponent canvas, string method)
        => typeof(UICanvasComponent).GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, null);

    private static UIPublication? Candidate(UICanvasComponent canvas)
        => (UIPublication?)typeof(UICanvasComponent).GetField("_candidatePublication",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas);

    private static ulong PainterRevision(UICanvasComponent canvas)
        => (ulong)typeof(UICanvasComponent).GetField("_publicationPainterOrderRevision",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas)!;

    private static List<long> PainterOrder(UICanvasComponent canvas)
        => (List<long>)typeof(UICanvasComponent).GetField("_publicationPainterOrder",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas)!;
}
