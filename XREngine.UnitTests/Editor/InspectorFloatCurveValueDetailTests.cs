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
public sealed class InspectorFloatCurveValueDetailTests
{
    [TestCase("time")]
    [TestCase("incoming")]
    [TestCase("in-tangent")]
    [TestCase("out-tangent")]
    [TestCase("in-weight")]
    [TestCase("out-weight")]
    [TestCase("in-mode")]
    [TestCase("out-mode")]
    [TestCase("value-links")]
    [TestCase("direction-links")]
    [TestCase("magnitude-links")]
    [TestCase("in-weighted")]
    [TestCase("out-weighted")]
    public void ExplicitMarkedHandleActionsUseExactTargetsAndOneUndo(string operation)
    {
        using UITestShaderScope shaders = new();
        Undo.ClearHistory();
        var a = Curve(out var first, out var unrelated);
        var b = Curve(out var second, out _);
        foreach (FloatKeyframe key in new[] { first, second })
        {
            key.SyncInOutValues = false;
            key.SyncInOutTangentDirections = false;
            key.SyncInOutTangentMagnitudes = false;
            key.InterpolationTypeIn = EVectorInterpType.Hermite;
            key.InterpolationTypeOut = EVectorInterpType.Hermite;
            key.WeightedMode = EKeyframeWeightedMode.Both;
            key.InWeight = 0.4f;
            key.OutWeight = 0.6f;
        }
        first.TryCaptureAuthoredState(out var beforeFirst);
        second.TryCaptureAuthoredState(out var beforeSecond);
        unrelated.TryCaptureAuthoredState(out var beforeUnrelated);
        SceneNode root = Build([new() { Curve = a }, new() { Curve = b }], out var detail);
        try
        {
            MarkBoth(root, detail, first, second);
            Field<UITextInputComponent>(detail, "_markedValue").Text = "0.25";
            switch (operation)
            {
                case "time": Call(detail, "SetMarkedTime"); break;
                case "incoming": Call(detail, "SetMarkedIncomingValue"); break;
                case "in-tangent": Call(detail, "SetMarkedTangent", false); break;
                case "out-tangent": Call(detail, "SetMarkedTangent", true); break;
                case "in-weight": Call(detail, "SetMarkedWeight", false); break;
                case "out-weight": Call(detail, "SetMarkedWeight", true); break;
                case "in-mode": Call(detail, "SetMarkedInterpolation", false, EVectorInterpType.Linear); break;
                case "out-mode": Call(detail, "SetMarkedInterpolation", true, EVectorInterpType.Linear); break;
                case "value-links": Call(detail, "SetMarkedSyncValues", true); break;
                case "direction-links": Call(detail, "SetMarkedSyncDirections", true); break;
                case "magnitude-links": Call(detail, "SetMarkedSyncMagnitudes", true); break;
                case "in-weighted": Call(detail, "SetMarkedWeightedSide", false, false); break;
                case "out-weighted": Call(detail, "SetMarkedWeightedSide", true, false); break;
            }
            foreach (FloatKeyframe key in new[] { first, second })
            {
                object actual = operation switch
                {
                    "time" => key.Second,
                    "incoming" => key.InValue,
                    "in-tangent" => key.InTangent,
                    "out-tangent" => key.OutTangent,
                    "in-weight" => key.InWeight,
                    "out-weight" => key.OutWeight,
                    "in-mode" => key.InterpolationTypeIn,
                    "out-mode" => key.InterpolationTypeOut,
                    "value-links" => key.SyncInOutValues,
                    "direction-links" => key.SyncInOutTangentDirections,
                    "magnitude-links" => key.SyncInOutTangentMagnitudes,
                    "in-weighted" => (key.WeightedMode & EKeyframeWeightedMode.In) != 0,
                    _ => (key.WeightedMode & EKeyframeWeightedMode.Out) != 0,
                };
                object expected = operation.EndsWith("-mode") ? EVectorInterpType.Linear :
                    operation.EndsWith("-links") ? true :
                    operation.EndsWith("-weighted") ? false : 0.25f;
                Assert.That(actual, Is.EqualTo(expected));
            }
            Assert.That(Undo.PendingUndo.Count, Is.EqualTo(1));
            Assert.That(Field<UITextInputComponent>(detail, "_markedValue").Text, Is.EqualTo("0.25"));
            first.TryCaptureAuthoredState(out var afterFirst);
            second.TryCaptureAuthoredState(out var afterSecond);
            Assert.That(Undo.TryUndo(), Is.True);
            first.TryCaptureAuthoredState(out var restoredFirst);
            second.TryCaptureAuthoredState(out var restoredSecond);
            Assert.That((restoredFirst, restoredSecond), Is.EqualTo((beforeFirst, beforeSecond)));
            Assert.That((first.Second, second.Second), Is.EqualTo((1f, 1f)));
            Assert.That(Undo.TryRedo(), Is.True);
            first.TryCaptureAuthoredState(out var redoneFirst);
            second.TryCaptureAuthoredState(out var redoneSecond);
            unrelated.TryCaptureAuthoredState(out var afterUnrelated);
            Assert.That((redoneFirst, redoneSecond), Is.EqualTo((afterFirst, afterSecond)));
            Assert.That((first.Second, second.Second), Is.EqualTo(operation == "time"
                ? (0.25f, 0.25f) : (1f, 1f)));
            Assert.That(afterUnrelated, Is.EqualTo(beforeUnrelated));
        }
        finally { root.Destroy(true); Undo.ClearHistory(); }
    }

    private static SceneNode Build(InspectorFloatCurveTestOwner[] owners, out object detail)
    {
        SceneNode root = new("Grouped value") { IsActiveSelf = false };
        root.SetTransform<UIBoundableTransform>();
        root.SetWorldContext(new TestWorldContext());
        var property = typeof(InspectorFloatCurveTestOwner).GetProperty(nameof(InspectorFloatCurveTestOwner.Curve))!;
        InspectorPropertyEditors.EngineTypes.CreatePropAnimFloatEditor(root, property, owners);
        root.IsActiveSelf = true;
        detail = root.GetComponent<InspectorFloatCurveTickComponent>()!.Refresh!.Target!;
        Refresh(root);
        return root;
    }

    private static T Field<T>(object detail, string name) =>
        (T)detail.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(detail)!;

    private static void Call(object detail, string name, params object[] arguments) =>
        detail.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(detail, arguments);

    private static void Refresh(SceneNode root)
    {
        var tick = root.GetComponent<InspectorFloatCurveTickComponent>()!;
        tick.Refresh?.Invoke();
        tick.Refresh?.Invoke();
    }

    private static PropAnimFloat Curve(out FloatKeyframe first, out FloatKeyframe second)
    {
        PropAnimFloat curve = new(2, false, true);
        first = new(1, 2, 0, EVectorInterpType.Linear);
        second = new(1, 3, 0, EVectorInterpType.Linear);
        curve.Keyframes.Add(first);
        curve.Keyframes.Add(second);
        return curve;
    }

    private static void Select(SceneNode root, object detail, FloatKeyframe key)
    {
        var table = Field<UIVirtualTableProvider<FloatKeyframe>>(detail, "_table");
        var keys = Field<Dictionary<ulong, FloatKeyframe>>(detail, "_keys");
        table.SelectedId = keys.Single(pair => ReferenceEquals(pair.Value, key)).Key;
        Refresh(root);
    }

    private static void MarkBoth(SceneNode root, object detail, FloatKeyframe first, FloatKeyframe second)
    {
        Select(root, detail, first);
        Call(detail, "ToggleRemovalMark");
        Refresh(root);
        Call(detail, "ChangeRemovalCurve", 1);
        Refresh(root);
        Select(root, detail, second);
        Call(detail, "ToggleRemovalMark");
        Refresh(root);
    }

    [Test]
    public void ExplicitMarkedValuesRespectLinksAndRestoreThroughOneUndo()
    {
        using UITestShaderScope shaders = new();
        Undo.ClearHistory();
        var a = Curve(out var first, out var equalTime);
        var b = Curve(out var second, out _);
        second.SyncInOutValues = false;
        second.InValue = -4;
        first.TryCaptureAuthoredState(out var beforeFirst);
        second.TryCaptureAuthoredState(out var beforeSecond);
        SceneNode root = Build([new() { Curve = a }, new() { Curve = b }], out var detail);
        try
        {
            MarkBoth(root, detail, first, second);
            Field<UITextInputComponent>(detail, "_markedValue").Text = "17";
            Call(detail, "SetMarkedValue");
            Assert.That((first.InValue, first.OutValue, second.InValue, second.OutValue),
                Is.EqualTo((17f, 17f, -4f, 17f)));
            Assert.That(equalTime.OutValue, Is.EqualTo(3f));
            Assert.That(Undo.TryUndo(), Is.True);
            first.TryCaptureAuthoredState(out var restoredFirst);
            second.TryCaptureAuthoredState(out var restoredSecond);
            Assert.That(restoredFirst, Is.EqualTo(beforeFirst));
            Assert.That(restoredSecond, Is.EqualTo(beforeSecond));
            Assert.That(Undo.CanUndo, Is.False);
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That((first.OutValue, second.OutValue), Is.EqualTo((17f, 17f)));
        }
        finally { root.Destroy(true); Undo.ClearHistory(); }
    }

    [TestCase("")]
    [TestCase("NaN")]
    [TestCase("Infinity")]
    public void InvalidDraftCannotChangeMarkedKeys(string draft)
    {
        using UITestShaderScope shaders = new();
        Undo.ClearHistory();
        var a = Curve(out var first, out _);
        var b = Curve(out var second, out _);
        SceneNode root = Build([new() { Curve = a }, new() { Curve = b }], out var detail);
        try
        {
            MarkBoth(root, detail, first, second);
            Field<UITextInputComponent>(detail, "_markedValue").Text = draft;
            Call(detail, "SetMarkedValue");
            Assert.That((first.OutValue, second.OutValue), Is.EqualTo((2f, 2f)));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { root.Destroy(true); Undo.ClearHistory(); }
    }

    [Test]
    public void RetargetedInputCannotWriteTheOldMarkedCurves()
    {
        using UITestShaderScope shaders = new();
        Undo.ClearHistory();
        var a = Curve(out var first, out _);
        var b = Curve(out var second, out _);
        SceneNode root = Build([new() { Curve = a }, new() { Curve = b }], out var detail);
        try
        {
            MarkBoth(root, detail, first, second);
            var input = Field<UITextInputComponent>(detail, "_markedValue");
            input.Text = "17";
            input.SceneNode.Transform.SetParent(root.Transform, false, EParentAssignmentMode.Immediate);
            Call(detail, "SetMarkedValue");
            Assert.That((first.OutValue, second.OutValue), Is.EqualTo((2f, 2f)));
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { root.Destroy(true); Undo.ClearHistory(); }
    }

    [Test]
    public void CompletionPreservesNewerDraftAndMarkIntent()
    {
        using UITestShaderScope shaders = new();
        Undo.ClearHistory();
        var a = Curve(out var first, out _);
        var b = Curve(out var second, out _);
        SceneNode root = Build([new() { Curve = a }, new() { Curve = b }], out var detail);
        Action<BaseKeyframeTrack>? callback = null;
        try
        {
            MarkBoth(root, detail, first, second);
            var input = Field<UITextInputComponent>(detail, "_markedValue");
            var marks = Field<InspectorFloatCurveKeyMarks>(detail, "_removalMarks");
            input.Text = "17";
            callback = _ =>
            {
                input.Text = "29";
                marks.Mark(0, first);
                marks.Mark(1, second);
            };
            b.Keyframes.Changed += callback;
            Call(detail, "SetMarkedValue");
            Assert.That((first.OutValue, second.OutValue), Is.EqualTo((17f, 17f)));
            Assert.That(input.Text, Is.EqualTo("29"));
            Assert.That(marks.GetMarkedKey(0), Is.SameAs(first));
            Assert.That(marks.GetMarkedKey(1), Is.SameAs(second));
        }
        finally
        {
            if (callback is not null) b.Keyframes.Changed -= callback;
            root.Destroy(true);
            Undo.ClearHistory();
        }
    }

    [Test]
    public void MissingMarksAndUnchangedValuesDoNotCreateHistory()
    {
        using UITestShaderScope shaders = new();
        Undo.ClearHistory();
        var a = Curve(out var first, out _);
        var b = Curve(out var second, out _);
        SceneNode root = Build([new() { Curve = a }, new() { Curve = b }], out var detail);
        try
        {
            var input = Field<UITextInputComponent>(detail, "_markedValue");
            input.Text = "17";
            Call(detail, "SetMarkedValue");
            Assert.That((first.OutValue, second.OutValue), Is.EqualTo((2f, 2f)));
            MarkBoth(root, detail, first, second);
            input.Text = "2";
            Call(detail, "SetMarkedValue");
            Assert.That(Undo.CanUndo, Is.False);
            Assert.That(Field<InspectorFloatCurveKeyMarks>(detail, "_removalMarks").MarkedCount,
                Is.EqualTo(2));
        }
        finally { root.Destroy(true); Undo.ClearHistory(); }
    }
}
