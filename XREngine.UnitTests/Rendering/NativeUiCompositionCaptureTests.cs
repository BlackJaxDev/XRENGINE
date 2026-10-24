using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class NativeUiCompositionCaptureTests
{
    private UITestShaderScope _shaderScope = null!;

    [SetUp]
    public void SetUp() => _shaderScope = new UITestShaderScope();

    [TearDown]
    public void TearDown() => _shaderScope.Dispose();

    [Test]
    public void GalleryChromeKeepsSegmentWhenFpsOverlayChanges()
    {
        string? prior = Environment.GetEnvironmentVariable("XRE_UI_CONTROL_GALLERY");
        UICanvasTransform transform = new() { Width = 1280, Height = 720 };
        SceneNode root = new("Native composition capture", transform);
        root.SetWorldContext(new TestWorldContext());
        UIPublication? firstPublication = null;
        UIPublication? secondPublication = null;
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            Environment.SetEnvironmentVariable("XRE_UI_CONTROL_GALLERY", "1");
            typeof(EditorUnitTests.UserInterface).GetMethod("CreateControlGallery",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [root]);
            UITextComponent fps = EditorUnitTests.UserInterface.AddFPSText(null, root);
            fps.Text = "FPS 1";
            SceneNode gallery = root.Transform.Children
                .Select(child => child.SceneNode)
                .First(node => node?.Name == "Native control gallery")!;
            UICompositionOwner owner = new(
                ((UITransform)gallery.Transform).ElementIdentity,
                EUICompositionLayerKind.StaticChrome);
            UICompositionOwner fpsOwner = new(
                fps.BoundableTransform.ElementIdentity,
                EUICompositionLayerKind.Overlay);

            Capture(canvas, transform);
            firstPublication = RetainedCandidate(canvas);
            UICompositionSegment firstChrome = FindSegment(firstPublication, owner);
            UICompositionSegment firstFps = FindSegment(firstPublication, fpsOwner);
            firstChrome.Records.Length.ShouldBeGreaterThan(0);
            firstFps.Records.Length.ShouldBeGreaterThan(0);

            fps.Text = "FPS 2";
            Capture(canvas, transform);
            secondPublication = RetainedCandidate(canvas);
            UICompositionSegment secondChrome = FindSegment(secondPublication, owner);
            UICompositionSegment secondFps = FindSegment(secondPublication, fpsOwner);
            secondChrome.Identity.ShouldBe(firstChrome.Identity);
            secondChrome.Revision.ShouldBe(firstChrome.Revision);
            secondFps.Identity.ShouldBe(firstFps.Identity);
            secondFps.Revision.ShouldBeGreaterThan(firstFps.Revision);
            secondFps.Records.SequenceEqual(firstFps.Records).ShouldBeFalse();
        }
        finally
        {
            secondPublication?.Release();
            firstPublication?.Release();
            Environment.SetEnvironmentVariable("XRE_UI_CONTROL_GALLERY", prior);
            root.Destroy(true);
        }
    }

    private static UICompositionSegment FindSegment(UIPublication publication,
        UICompositionOwner owner)
    {
        foreach (UICompositionSegment segment in publication.CompositionSegments)
            if (segment.Owner == owner)
                return segment;
        throw new AssertionException("The canvas did not publish the expected composition segment.");
    }

    private static void Capture(UICanvasComponent canvas,
        UICanvasTransform transform)
    {
        transform.UpdateLayoutAsync();
        SpinWait.SpinUntil(() => transform.PumpLayoutResults(),
            TimeSpan.FromSeconds(5)).ShouldBeTrue();
        typeof(UICanvasComponent).GetMethod("CapturePublicationCandidate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, null);
    }

    private static UIPublication RetainedCandidate(UICanvasComponent canvas)
    {
        UIPublication publication = (UIPublication?)typeof(UICanvasComponent).GetField(
            "_candidatePublication", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(canvas) ?? throw new AssertionException(
                "The canvas did not publish a candidate.");
        publication.Retain();
        return publication;
    }
}
