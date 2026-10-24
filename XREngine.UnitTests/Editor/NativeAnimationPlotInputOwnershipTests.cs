using System.Reflection;
using System.Numerics;
using NUnit.Framework;
using XREngine.Animation;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeAnimationPlotInputOwnershipTests
{
    [Test]
    public void BasePressAndReleaseCallbacksCannotRestoreRetiredOwner()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("Plot canvas", new UICanvasTransform())
        {
            IsActiveSelf = false
        };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        AnimationPlotInputComponent input = root.NewChild("Plot")
            .AddComponent<AnimationPlotInputComponent>()!;
        AnimationPanel first = new();
        AnimationPanel nested = new();
        input.Panel = first;
        AnimationMember member = new("Value");
        AnimationClip clip = new(member) { LengthInSeconds = 2 };
        AnimationPlotPointerSnapshot snapshot = new(first, input, clip,
            1, 1, 1, 100, 100, 0, 2, false, [], []);
        UIPointerGeometry geometry = new(new Vector2(100, 100), Metadata: snapshot);
        bool pressRebound = false;
        input.PropertyChanged += (_, change) =>
        {
            if (pressRebound || change.PropertyName != nameof(input.IsPressed) ||
                !input.IsPressed)
                return;
            pressRebound = true;
            input.Panel = nested;
            input.Panel = first;
        };
        try
        {
            root.IsActiveSelf = true;
            Assert.That(input.CanInteract, Is.True);
            input.PointerPressed(new Vector2(20, 20), in geometry);
            Assert.That(pressRebound, Is.True);
            Assert.That(GetPressedSnapshot(input), Is.Null);
            input.PointerCancelled();

            input.PointerPressed(new Vector2(20, 20), in geometry);
            Assert.That(GetPressedSnapshot(input), Is.SameAs(snapshot));
            bool releaseRebound = false;
            input.PropertyChanged += (_, change) =>
            {
                if (releaseRebound || change.PropertyName != nameof(input.IsPressed) ||
                    input.IsPressed)
                    return;
                releaseRebound = true;
                input.Panel = nested;
                input.Panel = first;
            };
            input.PointerReleased(new Vector2(20, 20), true, in geometry);
            Assert.That(releaseRebound, Is.True);
            Assert.That(typeof(AnimationPlotInputComponent).GetField("_lastClickClip",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(input), Is.Null);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void VetoedBasePressDoesNotRetainPlotMetadata()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("Plot canvas", new UICanvasTransform())
        {
            IsActiveSelf = false
        };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        AnimationPlotInputComponent input = root.NewChild("Plot")
            .AddComponent<AnimationPlotInputComponent>()!;
        AnimationPanel panel = new();
        input.Panel = panel;
        AnimationClip clip = new(new AnimationMember("Value"))
        {
            LengthInSeconds = 2
        };
        AnimationPlotPointerSnapshot snapshot = new(panel, input, clip,
            1, 1, 1, 100, 100, 0, 2, false, [], []);
        UIPointerGeometry geometry = new(new Vector2(100, 100), Metadata: snapshot);
        input.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(input.IsPressed) &&
                change is XRPropertyChangingEventArgs<bool> pressed && pressed.NewValue)
                pressed.AllowChange = false;
        };
        try
        {
            root.IsActiveSelf = true;
            Assert.That(input.CanInteract, Is.True);
            input.PointerPressed(new Vector2(20, 20), in geometry);
            Assert.That(input.IsPressed, Is.False);
            Assert.That(GetPressedSnapshot(input), Is.Null);
        }
        finally { root.Destroy(true); }
    }

    private static object? GetPressedSnapshot(AnimationPlotInputComponent input)
        => typeof(AnimationPlotInputComponent)
            .GetField("_pressedSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(input);

    [Test]
    public void ReentrantPanelAssignmentDoesNotCommitStaleOwner()
    {
        AnimationPlotInputComponent input = new();
        AnimationPanel first = new();
        AnimationPanel nested = new();
        AnimationPanel stale = new();
        input.Panel = first;
        bool recursed = false;
        input.PropertyChanging += (_, change) =>
        {
            if (recursed || change.PropertyName != nameof(AnimationPlotInputComponent.Panel))
                return;
            recursed = true;
            input.Panel = nested;
            input.Panel = first;
        };

        ulong oldGeneration = input.DispatchGeneration;
        long oldRevision = input.InteractionRevision;
        input.Panel = stale;

        Assert.That(input.Panel, Is.SameAs(first));
        Assert.That(input.DispatchGeneration, Is.GreaterThan(oldGeneration));
        bool staleDispatch = (bool)typeof(AnimationPlotInputComponent)
            .GetMethod("HasSameOwner", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(input, [first, oldRevision, oldGeneration])!;
        Assert.That(staleDispatch, Is.False);
    }
}
