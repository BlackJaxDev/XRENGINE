using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Editor;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Physics;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeHierarchyLifetimeTests
{
    [Test]
    public void DirectDestroyReleasesOwnerSubscriptionsAndStoredRowCallbacks()
    {
        using UITestShaderScope shaders = new();
        int initialDrops = DropTargetCount;
        SceneNode canvas = CreateCanvas(out UITestCanvasInputProbe input);
        SceneNode source = new("Source");
        try
        {
            HierarchyPanel panel = CreatePanel(canvas, source);
            SceneNode panelNode = panel.SceneNode;
            UIListTransform list = RealizeRows(panel);
            TransformBase[] children = panel.SceneNode.Transform.Children.ToArray();
            object[] rows = RowStates(panel);
            var callbacks = CaptureRowCallbacks(rows);
            UIButtonComponent firstButton = Field<UIButtonComponent>(rows[0], "Button");
            Action<UIInteractableComponent> otherOwnerCallback = _ => { };
            firstButton.InteractAction += otherOwnerCallback;
            EventList<TransformBase> originalChildren = source.Transform.Children;
            object oldBinding = Field<IDictionary>(panel, "_sourceListeners")[source]!;
            Delegate oldAdded = Field<Delegate>(oldBinding, "Added");
            Delegate oldRemoved = Field<Delegate>(oldBinding, "Removed");
            source.SetTransform<UIBoundableTransform>();
            Assert.That(EventCallbacks(originalChildren, "PostAnythingAdded"), Does.Not.Contain(oldAdded));
            Assert.That(EventCallbacks(originalChildren, "PostAnythingRemoved"), Does.Not.Contain(oldRemoved));
            EventList<TransformBase> currentChildren = source.Transform.Children;
            Delegate[] sourceAdded = EventCallbacks(currentChildren, "PostAnythingAdded");
            Delegate[] sourceRemoved = EventCallbacks(currentChildren, "PostAnythingRemoved");
            input.FocusedComponent = Field<UITextInputComponent>(rows[0], "RenameInput");

            Assert.That(SelectionSubscriberCount(panel), Is.EqualTo(1));
            Assert.That(input.SubscriberCount, Is.EqualTo(1));
            Assert.That(HasTick(panel, "PollVirtualTreeSources"), Is.True);
            Assert.That(callbacks, Has.Count.EqualTo(rows.Length * 12));
            Assert.That(DropTargetCount, Is.EqualTo(initialDrops + rows.Length + 1));

            panel.Destroy(true);

            Assert.That(panel.IsDestroyed, Is.True);
            AssertReleased(panel, input, list, initialDrops);
            Assert.That(input.FocusedComponent, Is.Null);
            Assert.That(panelNode.Transform.Children.ToArray(), Is.EqualTo(children));
            Assert.That(children.All(child => ReferenceEquals(child.Parent, panelNode.Transform)), Is.True);
            foreach (var (owner, eventName, callback) in callbacks)
                Assert.That(EventCallbacks(owner, eventName), Does.Not.Contain(callback), eventName);
            Assert.That(EventCallbacks(firstButton, "InteractAction"), Does.Contain(otherOwnerCallback));
            Assert.That(EventCallbacks(currentChildren, "PostAnythingAdded").Length,
                Is.EqualTo(sourceAdded.Length - 1));
            Assert.That(EventCallbacks(currentChildren, "PostAnythingRemoved").Length,
                Is.EqualTo(sourceRemoved.Length - 1));

            panel.RemakeChildren();
            panel.RootNodes = [source];
            Invoke(panel, "OnComponentActivated");
            Invoke(panel, "CompleteDeferredHierarchyWork");
            Assert.That(panelNode.Transform.Children.ToArray(), Is.EqualTo(children));
            AssertReleased(panel, input, list, initialDrops);
        }
        finally
        {
            canvas.Destroy(true);
            source.Destroy(true);
        }
    }

    [Test]
    public void StopUnsubscribesTheOriginalCanvasAndActivationBindsTheCurrentCanvasOnce()
    {
        using UITestShaderScope shaders = new();
        SceneNode firstCanvas = CreateCanvas(out UITestCanvasInputProbe firstInput);
        SceneNode secondCanvas = CreateCanvas(out UITestCanvasInputProbe secondInput);
        SceneNode source = new("Source");
        try
        {
            HierarchyPanel panel = CreatePanel(firstCanvas, source);
            UIListTransform firstList = RealizeRows(panel);
            TransformBase[] children = panel.SceneNode.Transform.Children.ToArray();
            panel.SceneNode.Parent = secondCanvas;

            Invoke(panel, "OnComponentDeactivated");

            Assert.That(firstInput.SubscriberCount, Is.Zero);
            Assert.That(secondInput.SubscriberCount, Is.Zero);
            Assert.That(firstList.VirtualProvider, Is.Null);
            Assert.That(panel.SceneNode.Transform.Children.ToArray(), Is.EqualTo(children));
            Assert.That(children.All(child => !child.SceneNode!.IsDestroyed), Is.True);

            Invoke(panel, "OnComponentActivated");
            Invoke(panel, "OnComponentActivated");

            Assert.That(firstInput.SubscriberCount, Is.Zero);
            Assert.That(secondInput.SubscriberCount, Is.EqualTo(1));
            Assert.That(SelectionSubscriberCount(panel), Is.EqualTo(1));
            Assert.That(HasTick(panel, "PollVirtualTreeSources"), Is.True);
            Assert.That(Field<UIListTransform>(panel, "_treeListTransform"), Is.Not.SameAs(firstList));
            panel.Destroy(true);
            Assert.That(secondInput.SubscriberCount, Is.Zero);
        }
        finally
        {
            firstCanvas.Destroy(true);
            secondCanvas.Destroy(true);
            source.Destroy(true);
        }
    }

    [Test]
    public void ActivationDuringStopWaitsUntilTheRowSubtreeCanBeRebuilt()
    {
        using UITestShaderScope shaders = new();
        SceneNode canvas = CreateCanvas(out UITestCanvasInputProbe input);
        SceneNode source = new("Source");
        try
        {
            HierarchyPanel panel = CreatePanel(canvas, source);
            UIListTransform list = RealizeRows(panel);
            TransformBase[] children = panel.SceneNode.Transform.Children.ToArray();
            bool requested = false;
            list.VirtualRowRecycling += (_, _) =>
            {
                if (requested)
                    return;
                requested = true;
                Invoke(panel, "OnComponentActivated");
            };

            Invoke(panel, "OnComponentDeactivated");

            Assert.That(requested, Is.True);
            Assert.That(input.SubscriberCount, Is.Zero);
            Assert.That(panel.SceneNode.Transform.Children.ToArray(), Is.EqualTo(children));
            Assert.That(HasTick(panel, "CompleteDeferredHierarchyWork"), Is.True);
            Assert.That(HasTick(panel, "PollVirtualTreeSources"), Is.False);

            Invoke(panel, "CompleteDeferredHierarchyWork");

            Assert.That(input.SubscriberCount, Is.EqualTo(1));
            Assert.That(SelectionSubscriberCount(panel), Is.EqualTo(1));
            Assert.That(HasTick(panel, "CompleteDeferredHierarchyWork"), Is.False);
            Assert.That(HasTick(panel, "PollVirtualTreeSources"), Is.True);
            Assert.That(Field<UIListTransform>(panel, "_treeListTransform"), Is.Not.SameAs(list));
            panel.Destroy(true);
        }
        finally
        {
            canvas.Destroy(true);
            source.Destroy(true);
        }
    }

    [Test]
    public void ThrowingRecycleCallbackStillReleasesEveryOwnerAndCannotReactivateDestroyingPanel()
    {
        using UITestShaderScope shaders = new();
        int initialDrops = DropTargetCount;
        SceneNode canvas = CreateCanvas(out UITestCanvasInputProbe input);
        SceneNode source = new("Source");
        try
        {
            HierarchyPanel panel = CreatePanel(canvas, source);
            UIListTransform list = RealizeRows(panel);
            var callbacks = CaptureRowCallbacks(RowStates(panel));
            InvalidOperationException expected = new("recycle failed");
            int recycled = 0;
            list.VirtualRowRecycling += (_, _) =>
            {
                Invoke(panel, "OnComponentActivated");
                throw expected;
            };
            list.VirtualRowRecycling += (_, _) => recycled++;

            AggregateException? error = Assert.Throws<AggregateException>(() => panel.Destroy(true));

            Assert.That(error!.InnerExceptions[0], Is.SameAs(expected));
            Assert.That(recycled, Is.GreaterThan(1));
            AssertReleased(panel, input, list, initialDrops);
            foreach (var (owner, eventName, callback) in callbacks)
                Assert.That(EventCallbacks(owner, eventName), Does.Not.Contain(callback), eventName);
            Invoke(panel, "OnComponentActivated");
            panel.RemakeChildren();
            Assert.That(Field<object?>(panel, "_virtualTree"), Is.Null);
            Assert.That(HasTick(panel, "CompleteDeferredHierarchyWork"), Is.False);
            Assert.DoesNotThrow(() => panel.Destroy(true));
        }
        finally
        {
            canvas.Destroy(true);
            source.Destroy(true);
        }
    }

    private static SceneNode CreateCanvas(out UITestCanvasInputProbe input)
    {
        UICanvasTransform transform = new();
        transform.SetSize(new Vector2(400, 400));
        SceneNode canvas = new("Hierarchy canvas", transform) { IsActiveSelf = false };
        canvas.SetWorldContext(new TestWorldContext());
        canvas.AddComponent<UICanvasComponent>();
        input = canvas.AddComponent<UITestCanvasInputProbe>()!;
        return canvas;
    }

    private static HierarchyPanel CreatePanel(SceneNode parent, SceneNode source)
    {
        SceneNode node = new(parent, "Hierarchy");
        HierarchyPanel panel = node.AddComponent<HierarchyPanel>()!;
        panel.RootNodes = [source];
        Invoke(panel, "OnComponentActivated");
        return panel;
    }

    private static UIListTransform RealizeRows(HierarchyPanel panel)
    {
        UIListTransform list = Field<UIListTransform>(panel, "_treeListTransform");
        list.SynchronizeVirtualRows(400);
        Assert.That(RowStates(panel).Length, Is.GreaterThan(1));
        return list;
    }

    private static object[] RowStates(HierarchyPanel panel)
        => Field<IDictionary>(panel, "_hierarchyRows").Values.Cast<object>().ToArray();

    private static List<(object Owner, string EventName, Delegate Callback)> CaptureRowCallbacks(object[] rows)
    {
        List<(object, string, Delegate)> callbacks = [];
        foreach (object row in rows)
        {
            Add("Button", "InteractAction", "Click");
            Add("Arrow", "InteractAction", "ArrowClick");
            Add("Toggle", "InteractAction", "ToggleClick");
            Add("Unload", "InteractAction", "UnloadClick");
            Add("RenameInput", "Submitted", "RenameSubmitted");
            Add("RenameInput", "Cancelled", "RenameCancelled");
            Add("Button", "MouseMove", "DragMove");
            Add("Button", "MouseDirectOverlapLeave", "DragLeave");
            Add("RenameInput", "PropertyChanged", "RenameTextChanged");
            Add("Button", "NavigationRequested", "NavigationRequested");
            Add("Button", "ExpansionChanged", "ExpansionChanged");
            Add("Button", "GotFocus", "Focused");

            void Add(string componentName, string eventName, string callbackName)
            {
                object owner = Field<object>(row, componentName);
                Delegate callback = Field<Delegate>(row, callbackName);
                Assert.That(EventCallbacks(owner, eventName), Does.Contain(callback));
                callbacks.Add((owner, eventName, callback));
            }
        }
        return callbacks;
    }

    private static void AssertReleased(HierarchyPanel panel, UITestCanvasInputProbe input,
        UIListTransform list, int initialDrops)
    {
        Assert.That(input.SubscriberCount, Is.Zero);
        Assert.That(SelectionSubscriberCount(panel), Is.Zero);
        Assert.That(HasTick(panel, "PollVirtualTreeSources"), Is.False);
        Assert.That(list.VirtualProvider, Is.Null);
        Assert.That(RowStates(panel), Is.Empty);
        Assert.That(Field<IDictionary>(panel, "_sourceListeners"), Is.Empty);
        Assert.That(DropTargetCount, Is.EqualTo(initialDrops));
    }

    private static int DropTargetCount
        => ((IList)typeof(EditorDragDropUtility).GetField("_dropTargets",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Count;

    private static int SelectionSubscriberCount(HierarchyPanel panel)
        => (typeof(Selection).GetField("SelectionChanged", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null) as Delegate)?.GetInvocationList().Count(callback => ReferenceEquals(callback.Target, panel)) ?? 0;

    private static bool HasTick(HierarchyPanel panel, string name)
        => Field<IEnumerable<(ETickGroup Group, int Order, WorldTick Tick)>>(panel, "_tickCache")
            .Any(entry => ReferenceEquals(entry.Tick.Target, panel) && entry.Tick.Method.Name == name);

    private static T Field<T>(object owner, string name)
    {
        for (Type? type = owner.GetType(); type is not null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field)
                return (T)field.GetValue(owner)!;
        throw new MissingFieldException(owner.GetType().Name, name);
    }

    private static Delegate[] EventCallbacks(object owner, string name)
        => Field<Delegate?>(owner, name)?.GetInvocationList() ?? [];

    private static void Invoke(HierarchyPanel panel, string name)
    {
        try
        {
            typeof(HierarchyPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }
}
