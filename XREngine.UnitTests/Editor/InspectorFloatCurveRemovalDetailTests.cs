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
public sealed class InspectorFloatCurveRemovalDetailTests
{
    private static SceneNode Build(InspectorFloatCurveTestOwner[] owners, out object detail)
    {
        SceneNode root = new("Grouped removal") { IsActiveSelf = false };
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
    public void RowFocusDoesNotMarkAndExplicitTargetsRemoveInOneUndoAction()
    {
        using UITestShaderScope shaders = new();
        Undo.ClearHistory();
        var a = Curve(out var first, out var otherFirst);
        var b = Curve(out var second, out var otherSecond);
        SceneNode root = Build([new() { Curve = a }, new() { Curve = a }, new() { Curve = b }], out var detail);
        try
        {
            Select(root, detail, first);
            var marks = Field<InspectorFloatCurveRemovalMarks>(detail, "_removalMarks");
            Assert.That(marks.MarkedCount, Is.Zero);
            Call(detail, "RemoveMarkedKeys");
            Assert.That((a.Keyframes.Count, b.Keyframes.Count), Is.EqualTo((2, 2)));
            Assert.That(Undo.CanUndo, Is.False);
            MarkBoth(root, detail, otherFirst, second);
            Assert.That(marks.MarkedCount, Is.EqualTo(2));
            Assert.That(Field<UIButtonComponent>(detail, "_removeMarkedKeys").IsEnabled, Is.True);
            Call(detail, "RemoveMarkedKeys");
            Assert.That(a.Keyframes.First, Is.SameAs(first));
            Assert.That(b.Keyframes.First, Is.SameAs(otherSecond));
            Assert.That(Undo.PendingUndo, Has.Count.EqualTo(1));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That((a.Keyframes.Count, b.Keyframes.Count), Is.EqualTo((2, 2)));
        }
        finally { root.Destroy(true); Undo.ClearHistory(); }
    }

    [Test]
    public void SourceReplacementRejectsPreviouslyMarkedTargets()
    {
        using UITestShaderScope shaders = new();
        Undo.ClearHistory();
        var a = Curve(out var first, out _);
        var b = Curve(out var second, out _);
        InspectorFloatCurveTestOwner owner = new() { Curve = b };
        SceneNode root = Build([new() { Curve = a }, owner], out var detail);
        try
        {
            MarkBoth(root, detail, first, second);
            owner.Curve = new(2, false, true);
            Call(detail, "RemoveMarkedKeys");
            Assert.That((a.Keyframes.Count, b.Keyframes.Count), Is.EqualTo((2, 2)));
            Assert.That(Undo.CanUndo, Is.False);
            Refresh(root);
            Assert.That(Field<InspectorFloatCurveRemovalMarks>(detail, "_removalMarks").MarkedCount, Is.Zero);
        }
        finally { root.Destroy(true); Undo.ClearHistory(); }
    }

    [TestCase("table")]
    [TestCase("button")]
    [TestCase("provider")]
    [TestCase("queued-list")]
    [TestCase("queued-scroll")]
    [TestCase("queued-card-transform")]
    public void TransferredViewCannotRemoveAndRetirementPreservesForeignTable(string change)
    {
        using UITestShaderScope shaders = new();
        Undo.ClearHistory();
        var a = Curve(out var first, out _);
        var b = Curve(out var second, out _);
        SceneNode root = Build([new() { Curve = a }, new() { Curve = b }], out var detail);
        try
        {
            MarkBoth(root, detail, first, second);
            var list = Field<UIListTransform>(detail, "_list");
            var table = list.VirtualProvider;
            SceneNode foreign = root.NewChildWithTransform<UIBoundableTransform>(out _, "Foreign");
            if (change == "table") list.SceneNode!.Parent = foreign;
            else if (change == "button") Field<UIButtonComponent>(detail, "_removeMarkedKeys").SceneNode.Parent = foreign;
            else if (change == "queued-list") list.SceneNode!.Destroy();
            else if (change == "queued-scroll") list.SceneNode!.Parent!.Destroy();
            else if (change == "queued-card-transform") list.Parent!.Parent!.Destroy();
            else { list.VirtualProvider = null; list.VirtualProvider = table; }
            Call(detail, "RemoveMarkedKeys");
            Assert.That((a.Keyframes.Count, b.Keyframes.Count), Is.EqualTo((2, 2)));
            Assert.That(Undo.CanUndo, Is.False);
            root.GetComponent<InspectorListDetailRefreshComponent>()!.Refresh?.Invoke();
            Refresh(root);
            if (change is "table" or "provider")
            {
                Assert.That(list.VirtualProvider, Is.SameAs(table));
                Assert.That(table!.Count, Is.EqualTo(2));
            }
        }
        finally { root.Destroy(true); Undo.ClearHistory(); }
    }

    [Test]
    public void RetirementCallbackCannotClearATableTransferredByRecycling()
    {
        using UITestShaderScope shaders = new();
        var a = Curve(out _, out _);
        var b = Curve(out _, out _);
        SceneNode root = Build([new() { Curve = a }, new() { Curve = b }], out var detail);
        try
        {
            var list = Field<UIListTransform>(detail, "_list");
            var table = list.VirtualProvider;
            SceneNode foreign = root.NewChildWithTransform<UIBoundableTransform>(out _, "Foreign");
            list.SynchronizeVirtualRows(400);
            bool moved = false;
            list.VirtualRowRecycling += (_, _) =>
            {
                if (moved) return;
                moved = true;
                list.SceneNode!.Parent = foreign;
                list.VirtualProvider = table;
            };
            root.GetComponent<InspectorListDetailRefreshComponent>()!.Retire();
            Assert.That(moved, Is.True);
            Assert.That(list.VirtualProvider, Is.SameAs(table));
            Assert.That(table!.Count, Is.EqualTo(2));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void CallbackCanMarkANewerSurvivingKeyWithoutCompletionClearingIt()
    {
        using UITestShaderScope shaders = new();
        Undo.ClearHistory();
        var a = Curve(out var first, out var remaining);
        var b = Curve(out var second, out _);
        SceneNode root = Build([new() { Curve = a }, new() { Curve = b }], out var detail);
        try
        {
            MarkBoth(root, detail, first, second);
            bool changed = false;
            a.Keyframes.Changed += _ =>
            {
                if (changed) return;
                changed = true;
                Refresh(root);
                Call(detail, "ChangeRemovalCurve", -1);
                Refresh(root);
                Select(root, detail, remaining);
                Call(detail, "ToggleRemovalMark");
                Refresh(root);
            };
            Call(detail, "RemoveMarkedKeys");
            Assert.That(changed, Is.True);
            Assert.That(Field<InspectorFloatCurveRemovalMarks>(detail, "_removalMarks").GetMarkedKey(0), Is.SameAs(remaining));
            Assert.That((a.Keyframes.Count, b.Keyframes.Count), Is.EqualTo((1, 1)));
        }
        finally { root.Destroy(true); Undo.ClearHistory(); }
    }

    [Test]
    public void SharedSingleCurveKeepsItsOriginalRemoveWorkflow()
    {
        using UITestShaderScope shaders = new();
        Undo.ClearHistory();
        var curve = Curve(out var key, out var remaining);
        SceneNode root = Build([new() { Curve = curve }, new() { Curve = curve }], out var detail);
        try
        {
            Assert.That(Field<SceneNode>(detail, "_groupRemovalControls").GetTransformAs<UITransform>(true)!.Visibility,
                Is.EqualTo(EVisibility.Collapsed));
            Select(root, detail, key);
            Call(detail, "RemoveKey");
            Assert.That(curve.Keyframes.First, Is.SameAs(remaining));
            Assert.That(Undo.PendingUndo, Has.Count.EqualTo(1));
        }
        finally { root.Destroy(true); Undo.ClearHistory(); }
    }
}
