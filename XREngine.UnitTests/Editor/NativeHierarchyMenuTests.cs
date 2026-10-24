using System.Collections;
using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Rendering;
using XREngine.UnitTests.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeHierarchyMenuTests
{
    private UITestShaderScope _shaders = null!;
    private SceneNode[] _selection = [];

    [SetUp]
    public void SetUp()
    {
        _shaders = new();
        _selection = Selection.SceneNodes;
        Selection.Clear();
        Undo.ClearHistory();
        Assert.That(Edits.IsEmpty, Is.True);
    }

    [TearDown]
    public void TearDown()
    {
        Edits.Clear();
        Undo.ClearHistory();
        Selection.SceneNodes = _selection;
        _shaders.Dispose();
    }

    [TestCase("Arrow")]
    [TestCase("Toggle")]
    [TestCase("RenameInput")]
    public void AuxiliaryHitsUseTheLeasedAncestorAndTheAcceptedCanvasPoint(string control)
    {
        SceneNode source = new("Source");
        _ = new SceneNode(source, "Child");
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", new XRScene("Scene", source)));
        var (canvas, panel, input) = CreatePanel(world);
        try
        {
            object row = Row(panel, source);
            if (control == "RenameInput")
                Invoke(panel, "BeginRename", source);
            UIInteractableComponent target = Field<UIInteractableComponent>(row, control);
            panel.BoundableTransform.Translation = new Vector2(900, 700);
            input.RaiseRightClick(target);
            HierarchyMenuSession first = Session(panel)!;
            Assert.That(first.Node, Is.SameAs(source));
            Assert.That(first.AwaitingShow, Is.True);
            Tick(panel);
            UIContextMenuComponent menu = Menu(panel)!;
            Assert.That(menu.IsOpen, Is.True);
            Assert.That(menu.SceneNode.Parent, Is.SameAs(canvas));
            Assert.That(Field<Vector2>(menu, "_pendingPosition"), Is.EqualTo(input.PresentedPosition));

            input.RaiseRightClick(target);
            Assert.That(first.Owner, Is.Null);
            Assert.That(menu.IsOpen, Is.False);
            Tick(panel);
            Assert.That(menu.IsOpen, Is.True, "The old input dispatch must not dismiss the replacement menu.");
            Assert.That(Session(panel)!.Node, Is.SameAs(source));
        }
        finally { canvas.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void UnacceptedPointerAndReplacedRowLeaseCannotOpenAMenu()
    {
        SceneNode source = new("Source");
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", new XRScene("Scene", source)));
        var (canvas, panel, input) = CreatePanel(world);
        try
        {
            UIInteractableComponent target = Field<UIInteractableComponent>(Row(panel, source), "Button");
            input.AcceptPointer = false;
            input.RaiseRightClick(target);
            Assert.That(Session(panel), Is.Null);
            input.AcceptPointer = true;
            input.RaiseRightClick(target);
            HierarchyMenuSession pending = Session(panel)!;
            UIListTransform list = Field<UIListTransform>(panel, "_treeListTransform");
            IVirtualListProvider? provider = list.VirtualProvider;
            list.VirtualProvider = null;
            list.VirtualProvider = provider;
            list.SynchronizeVirtualRows(400);
            Tick(panel);
            Assert.That(Session(panel), Is.Null);
            Assert.That(pending.Owner, Is.Null);
            Assert.That(Menu(panel), Is.Null);
        }
        finally { canvas.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void AReplacementRequestedDuringShowKeepsItsLiveActions()
    {
        SceneNode first = new("First"), second = new("Second");
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", new XRScene("Scene", first, second)));
        var (canvas, panel, input) = CreatePanel(world);
        try
        {
            UIInteractableComponent firstButton = Field<UIInteractableComponent>(Row(panel, first), "Button");
            UIInteractableComponent secondButton = Field<UIInteractableComponent>(Row(panel, second), "Button");
            Open(panel, input, firstButton);
            UIContextMenuComponent menu = Menu(panel)!;
            menu.Hide();
            bool replaced = false;
            menu.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName != nameof(UIContextMenuComponent.IsOpen) || !menu.IsOpen || replaced)
                    return;
                replaced = true;
                input.RaiseRightClick(secondButton);
                Tick(panel);
            };
            input.RaiseRightClick(firstButton);
            Tick(panel);
            Assert.That(replaced, Is.True);
            Assert.That(Session(panel)!.Node, Is.SameAs(second));
            Tick(panel);
            Assert.That(menu.IsOpen, Is.True);
            Assert.That(Session(panel)!.AwaitingShow, Is.False);
            Item(menu, "Rename").Action!();
            Assert.That(Field<SceneNode?>(panel, "_nodePendingRename"), Is.SameAs(second));
        }
        finally { canvas.Destroy(true); first.Destroy(true); second.Destroy(true); }
    }

    [TestCase("Delete")]
    [TestCase("Add Child")]
    public void QueuedNodeActionsRejectAChangedParentAfterDismissal(string action)
    {
        SceneNode first = new("First"), second = new("Second"), child = new(first, "Child");
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", new XRScene("Scene", first, second)));
        var (canvas, panel, input) = CreatePanel(world);
        try
        {
            Open(panel, input, Field<UIInteractableComponent>(Row(panel, child), "Toggle"));
            UIContextMenuComponent menu = Menu(panel)!;
            HierarchyMenuSession session = Session(panel)!;
            Item(menu, action).Action!();
            menu.Hide();
            Assert.That(session.Context, Is.Null);
            child.Transform.SetParent(second.Transform, false, EParentAssignmentMode.Immediate);
            RunQueuedEdit();
            Assert.That(child.Parent, Is.SameAs(second));
            Assert.That(child.Transform.Children, Is.Empty);
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { canvas.Destroy(true); first.Destroy(true); second.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InterruptedMenuAttachmentRetiresOnlyItsCandidateAndAllowsTheReplacement(bool throwDuringAttach)
    {
        SceneNode first = new("First"), second = new("Second");
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", new XRScene("Scene", first, second)));
        var (canvas, panel, input) = CreatePanel(world);
        try
        {
            UIInteractableComponent firstButton = Field<UIInteractableComponent>(Row(panel, first), "Button");
            UIInteractableComponent secondButton = Field<UIInteractableComponent>(Row(panel, second), "Button");
            SceneNode? interrupted = null;
            UIContextMenuComponent? interruptedMenu = null;
            canvas.Transform.Children.PostAnythingAdded += transform =>
            {
                if (interrupted is not null || transform.SceneNode?.Name != "HierarchyContextMenu")
                    return;
                interrupted = transform.SceneNode;
                interruptedMenu = interrupted.GetComponent<UIContextMenuComponent>();
                input.RaiseRightClick(secondButton);
                Tick(panel);
                if (throwDuringAttach)
                    throw new InvalidOperationException("Expected popup attachment callback failure.");
            };
            input.RaiseRightClick(firstButton);
            if (throwDuringAttach)
                Assert.Throws<InvalidOperationException>(() => Tick(panel));
            else
                Tick(panel);
            Assert.That(interrupted, Is.Not.Null);
            Assert.That(interruptedMenu!.IsDestroyed, Is.True);
            Assert.That(interrupted!.IsDestroyQueued || interrupted.IsDestroyed, Is.True);
            Tick(panel);
            Assert.That(Menu(panel)!.IsOpen, Is.True);
            Assert.That(Menu(panel), Is.Not.SameAs(interruptedMenu));
            Assert.That(Session(panel)!.Node, Is.SameAs(second));
            Item(Menu(panel)!, "Rename").Action!();
            Assert.That(Field<SceneNode?>(panel, "_nodePendingRename"), Is.SameAs(second));
        }
        finally { canvas.Destroy(true); first.Destroy(true); second.Destroy(true); }
    }

    [Test]
    public void AcceptedSceneVisibilityActionSurvivesDismissalAndUsesUndo()
    {
        SceneNode source = new("Source");
        XRScene scene = new("Scene", source);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        var (canvas, panel, input) = CreatePanel(world);
        try
        {
            object row = Rows(panel).Single(value => ReferenceEquals(Field<XRScene?>(value, "Scene"), scene));
            Open(panel, input, Field<UIInteractableComponent>(row, "Unload"));
            UIContextMenuComponent menu = Menu(panel)!;
            Assert.That(Item(menu, "Unload Scene").Enabled, Is.True);
            Item(menu, "Hide Scene").Action!();
            menu.Hide();
            RunQueuedEdit();
            Assert.That(scene.IsVisible, Is.False);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(scene.IsVisible, Is.True);
        }
        finally { canvas.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void DismissalCannotRedirectAnOldClickToAReboundRow()
    {
        SceneNode first = new("First"), second = new("Second");
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", new XRScene("Scene", first, second)));
        var (canvas, panel, input) = CreatePanel(world);
        try
        {
            UIListTransform list = Field<UIListTransform>(panel, "_treeListTransform");
            list.VirtualOverscan = 0;
            list.SynchronizeVirtualRows(20);
            list.TryScrollToVirtualItem(Id(panel, first));
            list.SynchronizeVirtualRows(20);
            UIVirtualTreeRowButtonComponent button = Field<UIVirtualTreeRowButtonComponent>(Row(panel, first), "Button");
            Open(panel, input, button);
            bool dismissed = false;
            Menu(panel)!.Dismissed += () =>
            {
                dismissed = true;
                list.TryScrollToVirtualItem(Id(panel, second));
                list.SynchronizeVirtualRows(20);
            };
            button.OnInteract();
            Assert.That(dismissed, Is.True);
            Assert.That(button.UserData, Is.Not.SameAs(first));
            Assert.That(Selection.SceneNodes, Is.Empty);
        }
        finally { canvas.Destroy(true); first.Destroy(true); second.Destroy(true); }
    }

    [Test]
    public void DirectDestroyReleasesPendingSessionAndItsCanvasMenuNode()
    {
        SceneNode source = new("Source");
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", new XRScene("Scene", source)));
        var (canvas, panel, input) = CreatePanel(world);
        try
        {
            UIInteractableComponent target = Field<UIInteractableComponent>(Row(panel, source), "Button");
            Open(panel, input, target);
            UIContextMenuComponent menu = Menu(panel)!;
            SceneNode menuNode = menu.SceneNode;
            input.RaiseRightClick(target);
            HierarchyMenuSession pending = Session(panel)!;
            panel.Destroy(true);
            Tick(panel);
            Assert.That(pending.Owner, Is.Null);
            Assert.That(pending.Context, Is.Null);
            Assert.That(menu.IsDestroyed, Is.True);
            Assert.That(menuNode.IsDestroyQueued || menuNode.IsDestroyed, Is.True);
            Assert.That(Session(panel), Is.Null);
        }
        finally { canvas.Destroy(true); source.Destroy(true); }
    }

    private static (SceneNode Canvas, HierarchyPanel Panel, HierarchyMenuInputProbe Input) CreatePanel(RuntimeWorld world)
    {
        UICanvasTransform transform = new();
        transform.SetSize(new Vector2(400));
        SceneNode canvas = new("Hierarchy menu canvas", transform) { IsActiveSelf = false };
        canvas.SetWorldContext(world);
        canvas.AddComponent<UICanvasComponent>();
        HierarchyMenuInputProbe input = canvas.AddComponent<HierarchyMenuInputProbe>()!;
        HierarchyPanel panel = new SceneNode(canvas, "Hierarchy").AddComponent<HierarchyPanel>()!;
        canvas.IsActiveSelf = true;
        Field<UIListTransform>(panel, "_treeListTransform").SynchronizeVirtualRows(400);
        return (canvas, panel, input);
    }

    private static void Open(HierarchyPanel panel, HierarchyMenuInputProbe input, UIInteractableComponent target)
    {
        input.RaiseRightClick(target);
        Assert.That(Session(panel), Is.Not.Null);
        Tick(panel);
        Assert.That(Menu(panel)?.IsOpen, Is.True);
    }

    private static void Tick(HierarchyPanel panel) => Invoke(panel, "CompleteDeferredHierarchyWork");
    private static HierarchyMenuSession? Session(HierarchyPanel panel) => Field<HierarchyMenuSession?>(panel, "_contextMenuSession");
    private static UIContextMenuComponent? Menu(HierarchyPanel panel) => Field<UIContextMenuComponent?>(panel, "_contextMenu");
    private static ContextMenuItem Item(UIContextMenuComponent menu, string label)
        => Field<ContextMenuItem[]>(menu, "_items").Single(item => item.Label == label);
    private static IEnumerable<object> Rows(HierarchyPanel panel)
        => Field<IDictionary>(panel, "_hierarchyRows").Values.Cast<object>();
    private static object Row(HierarchyPanel panel, SceneNode node)
        => Rows(panel).Single(value => ReferenceEquals(Field<SceneNode?>(value, "Node"), node));
    private static ulong Id(HierarchyPanel panel, SceneNode node)
        => Field<Dictionary<Guid, ulong>>(panel, "_hierarchyIds")[node.ID];
    private static T Field<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static ConcurrentQueue<Action> Edits => (ConcurrentQueue<Action>)typeof(EditorImGuiUI)
        .GetField("_queuedSceneEdits", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static void RunQueuedEdit()
    {
        Assert.That(Edits.TryDequeue(out Action? edit), Is.True);
        edit!();
        Assert.That(Edits.IsEmpty, Is.True);
    }

    private static void Invoke(HierarchyPanel panel, string name, params object?[] args)
    {
        try { typeof(HierarchyPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }
}
