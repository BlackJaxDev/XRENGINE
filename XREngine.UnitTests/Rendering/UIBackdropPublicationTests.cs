using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIBackdropPublicationTests
{
    [Test]
    public void BackdropIsOptInAndEachPublicationFreezesConsumerStrength()
    {
        using UITestShaderScope shaders = new();
        UICanvasTransform transform = new() { Width = 400, Height = 300 };
        SceneNode root = new("Backdrop publication", transform);
        root.SetWorldContext(new TestWorldContext());
        UIPublication? first = null;
        UIPublication? second = null;
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            SceneNode panel = new(root) { Name = "Blur panel" };
            panel.SetTransform<UIBoundableTransform>().Width = 100;
            UIBackdropBlurComponent blur = panel.AddComponent<UIBackdropBlurComponent>()!;
            blur.Enabled.ShouldBeFalse();
            Capture(canvas, transform);
            first = RetainedCandidate(canvas);
            Find(first, blur).Backdrop.IsDefined.ShouldBeFalse();

            blur.Enabled = true;
            blur.BlurStrength = 3.0f;
            Capture(canvas, transform, requireFirstLayout: false);
            second = RetainedCandidate(canvas);
            UIRenderRecord frozen = Find(second, blur);
            frozen.Backdrop.IsDefined.ShouldBeTrue();
            frozen.Backdrop.BlurStrength.ShouldBe(3.0f);
            frozen.Texture.ShouldBeNull();

            blur.BlurStrength = 7.0f;
            frozen.Backdrop.BlurStrength.ShouldBe(3.0f);
        }
        finally
        {
            second?.Release();
            first?.Release();
            root.Destroy(true);
        }
    }

    private static UIRenderRecord Find(UIPublication publication, UIBackdropBlurComponent component)
    {
        foreach (ref readonly UIRenderRecord record in publication.Renders)
            if (record.Identity == component.BoundableTransform.ElementIdentity)
                return record;
        throw new AssertionException("The backdrop component has no publication record.");
    }

    private static void Capture(UICanvasComponent canvas, UICanvasTransform transform,
        bool requireFirstLayout = true)
    {
        transform.UpdateLayoutAsync();
        if (requireFirstLayout)
            SpinWait.SpinUntil(() => transform.PumpLayoutResults(),
                TimeSpan.FromSeconds(5)).ShouldBeTrue();
        else
            transform.PumpLayoutResults();
        typeof(UICanvasComponent).GetMethod("CapturePublicationCandidate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, null);
    }

    private static UIPublication RetainedCandidate(UICanvasComponent canvas)
    {
        UIPublication publication = (UIPublication?)typeof(UICanvasComponent).GetField(
            "_candidatePublication", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(canvas) ?? throw new AssertionException("No candidate publication was captured.");
        publication.Retain();
        return publication;
    }
}
