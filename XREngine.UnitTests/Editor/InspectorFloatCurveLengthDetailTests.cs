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
    public void DistinctSelectionPresentsLengthOnlyAndRejectsStaleInput()
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
            Assert.That(time.IsEnabled, Is.False);
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

    private static IEnumerable<UITextInputComponent> Inputs(SceneNode node)
    {
        if (node.GetComponent<UITextInputComponent>() is { } input) yield return input;
        foreach (TransformBase child in node.Transform.Children)
            if (child.SceneNode is { } childNode)
                foreach (UITextInputComponent nested in Inputs(childNode))
                    yield return nested;
    }
}
