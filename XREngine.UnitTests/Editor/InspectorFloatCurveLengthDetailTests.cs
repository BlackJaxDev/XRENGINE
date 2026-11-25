using System.Reflection;
using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Physics;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class InspectorFloatCurveLengthDetailTests
{
    [Test]
    public void DistinctSelectionPresentsLengthAndCommonTimeAndRejectsStaleInput()
    {
        using UITestShaderScope shaders = new();
        Undo.ClearHistory();
        PropAnimFloat first = new(2, false, true), second = new(3, false, true);
        InspectorFloatCurveTestOwner a = new() { Curve = first };
        InspectorFloatCurveTestOwner b = new() { Curve = second };
        PropertyInfo property = typeof(InspectorFloatCurveTestOwner).GetProperty(
            nameof(InspectorFloatCurveTestOwner.Curve))!;
        SceneNode root = new("Selected float curves") { IsActiveSelf = false };
        root.SetTransform<UIBoundableTransform>();
        root.SetWorldContext(new TestWorldContext());
        try
        {
            InspectorPropertyEditors.EngineTypes.CreatePropAnimFloatEditor(root,
                property, [a, b]);
            UITextInputComponent length = Inputs(root).Single(input =>
                input.AccessibilityLabel == "Length");
            UITextInputComponent time = Inputs(root).Single(input =>
                input.AccessibilityLabel == "Key time");
            UITextInputComponent value = Inputs(root).Single(input =>
                input.AccessibilityLabel == "Out value");
            root.IsActiveSelf = true;
            root.GetComponent<InspectorFloatCurveTickComponent>()!.Refresh?.Invoke();
            Assert.That(length.IsEnabled, Is.True);
            Assert.That(length.Text, Is.Empty);
            Assert.That(time.IsEnabled, Is.True);
            Assert.That(time.Text, Is.EqualTo("0"));
            Assert.That(value.IsEnabled, Is.False);

            length.Text = "5";
            length.UserSubmit();
            Assert.That((first.LengthInSeconds, second.LengthInSeconds),
                Is.EqualTo((5f, 5f)));
            Assert.That(Undo.PendingUndo, Has.Count.EqualTo(1));

            PropAnimFloat replacement = new(4, false, true);
            b.Curve = replacement;
            length.Text = "7";
            length.UserSubmit();
            Assert.That((first.LengthInSeconds, second.LengthInSeconds,
                replacement.LengthInSeconds), Is.EqualTo((5f, 5f, 4f)));
            root.GetComponent<InspectorFloatCurveTickComponent>()!.Refresh?.Invoke();
            length.Text = "6";
            length.UserSubmit();
            Assert.That((first.LengthInSeconds, second.LengthInSeconds,
                replacement.LengthInSeconds), Is.EqualTo((6f, 5f, 6f)));

            b.Curve = first;
            length.Text = "7";
            bool submittedDuringRebind = false;
            length.PropertyChanging += (_, change) =>
            {
                if (submittedDuringRebind || change.PropertyName != nameof(length.Text)) return;
                submittedDuringRebind = true;
                length.UserSubmit();
            };
            root.GetComponent<InspectorFloatCurveTickComponent>()!.Refresh?.Invoke();
            Assert.That(submittedDuringRebind, Is.True);
            Assert.That(first.LengthInSeconds, Is.EqualTo(6f));
            root.GetComponent<InspectorFloatCurveTickComponent>()!.Refresh?.Invoke();
            length.Text = "8";
            length.UserSubmit();
            Assert.That(first.LengthInSeconds, Is.EqualTo(8f));

            root.AdoptPersistentID(Guid.NewGuid());
            length.Text = "9";
            length.UserSubmit();
            Assert.That((first.LengthInSeconds, replacement.LengthInSeconds),
                Is.EqualTo((8f, 6f)));
            root.GetComponent<InspectorListDetailRefreshComponent>()!.Retire();
            length.Text = "10";
            length.UserSubmit();
            Assert.That((first.LengthInSeconds, replacement.LengthInSeconds),
                Is.EqualTo((8f, 6f)));
        }
        finally { root.Destroy(true); Undo.ClearHistory(); }
    }

    [Test]
    public void DistinctSelectionAddsAtSubmittedTimeOnlyForPresentedOwners()
    {
        using UITestShaderScope shaders = new();
        Undo.ClearHistory();
        PropAnimFloat first = new(2, false, true) { DefaultValue = 3f };
        PropAnimFloat second = new(3, false, true) { DefaultValue = 7f };
        InspectorFloatCurveTestOwner a = new() { Curve = first };
        InspectorFloatCurveTestOwner b = new() { Curve = second };
        PropertyInfo property = typeof(InspectorFloatCurveTestOwner).GetProperty(
            nameof(InspectorFloatCurveTestOwner.Curve))!;
        SceneNode root = new("Selected float curves") { IsActiveSelf = false };
        root.SetTransform<UIBoundableTransform>();
        root.SetWorldContext(new TestWorldContext());
        try
        {
            InspectorPropertyEditors.EngineTypes.CreatePropAnimFloatEditor(root,
                property, [a, b]);
            UITextInputComponent time = Inputs(root).Single(input =>
                input.AccessibilityLabel == "Key time");
            UITextInputComponent value = Inputs(root).Single(input =>
                input.AccessibilityLabel == "Out value");
            time.Text = "1";
            bool submittedDuringPresentation = false;
            time.PropertyChanging += (_, change) =>
            {
                if (submittedDuringPresentation || change.PropertyName != nameof(time.Text))
                    return;
                submittedDuringPresentation = true;
                time.UserSubmit();
            };
            root.GetComponent<InspectorListDetailRefreshComponent>()!.Refresh?.Invoke();
            root.IsActiveSelf = true;
            InspectorFloatCurveTickComponent tick =
                root.GetComponent<InspectorFloatCurveTickComponent>()!;
            tick.Refresh?.Invoke();
            Assert.That(submittedDuringPresentation, Is.True);
            Assert.That((first.Keyframes.Count, second.Keyframes.Count), Is.EqualTo((0, 0)));
            tick.Refresh?.Invoke();
            Assert.That((time.IsEnabled, value.IsEnabled), Is.EqualTo((true, false)));

            time.Text = "1";
            time.UserSubmit();
            Assert.That((first.Keyframes.Count, second.Keyframes.Count), Is.EqualTo((1, 1)));
            Assert.That((first.Keyframes.First!.OutValue, second.Keyframes.First!.OutValue),
                Is.EqualTo((3f, 7f)));
            Assert.That(Undo.PendingUndo, Has.Count.EqualTo(1));

            tick.Refresh?.Invoke();
            PropAnimFloat replacement = new(4, false, true);
            b.Curve = replacement;
            time.Text = "1.5";
            time.UserSubmit();
            Assert.That((first.Keyframes.Count, second.Keyframes.Count,
                replacement.Keyframes.Count), Is.EqualTo((1, 1, 0)));
            tick.Refresh?.Invoke();
            time.Text = "1.5";
            time.UserSubmit();
            Assert.That((first.Keyframes.Count, second.Keyframes.Count,
                replacement.Keyframes.Count), Is.EqualTo((2, 1, 1)));

            root.Generate();
            time.Text = "1.75";
            time.UserSubmit();
            Assert.That((first.Keyframes.Count, replacement.Keyframes.Count),
                Is.EqualTo((2, 1)));
        }
        finally { root.Destroy(true); Undo.ClearHistory(); }
    }

    private static IEnumerable<UITextInputComponent> Inputs(SceneNode node)
    {
        if (node.GetComponent<UITextInputComponent>() is { } input) yield return input;
        foreach (TransformBase child in node.Transform.Children)
            if (child.SceneNode is { } childNode)
                foreach (UITextInputComponent nested in Inputs(childNode))
                    yield return nested;
    }
}
