using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Editor;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.Scene.Physics.Jitter2;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Physics;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeHierarchyWorkflowTests
{
    private UITestShaderScope _shaders = null!;
    private SceneNode[] _previousSelection = [];

    [SetUp]
    public void SetUp()
    {
        _shaders = new UITestShaderScope();
        _previousSelection = Selection.SceneNodes;
        Selection.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        Selection.SceneNodes = _previousSelection;
        _shaders.Dispose();
    }

    [Test]
    public void ExternalNameAndActiveEditsRefreshRowsWithoutReplacingSourceBindings()
    {
        SceneNode first = new("First"), second = new("Second");
        var (canvas, panel) = CreatePanel([first, second]);
        try
        {
            UIListTransform list = Realize(panel);
            UIVirtualTreeProvider tree = Tree(panel);
            object firstBinding = Field<IDictionary>(panel, "_sourceListeners")[first]!;
            object secondBinding = Field<IDictionary>(panel, "_sourceListeners")[second]!;
            object row = Row(panel, first);
            UIBoundableTransform rowTransform = Field<UIBoundableTransform>(row, "Transform");
            Assert.That(list.TryGetVirtualRowLease(rowTransform, out UIVirtualRowLease lease), Is.True);

            first.Name = "Renamed elsewhere";
            first.IsActiveSelf = false;

            Assert.That(tree.TryGetNode(Id(panel, first), out UIVirtualTreeNode? item), Is.True);
            Assert.That(item!.Name, Is.EqualTo(first.Name));
            Assert.That(Field<UITextComponent>(row, "Label").Text, Is.EqualTo(first.Name));
            Assert.That(Field<UITextComponent>(row, "ToggleGlyph").Text, Is.Empty);
            first.IsActiveSelf = true;
            Assert.That(Field<UITextComponent>(row, "ToggleGlyph").Text, Is.EqualTo("\u2713"));
            first.CanDeactivate = false;
            Assert.That(Field<UIButtonComponent>(row, "Toggle").IsEnabled, Is.False);
            first.CanDeactivate = true;
            Assert.That(Field<UIButtonComponent>(row, "Toggle").IsEnabled, Is.True);
            Poll(panel);
            Assert.That(Field<IDictionary>(panel, "_sourceListeners")[first], Is.SameAs(firstBinding));
            Assert.That(Field<IDictionary>(panel, "_sourceListeners")[second], Is.SameAs(secondBinding));
            Assert.That(list.TryGetVirtualRowLease(rowTransform, out UIVirtualRowLease current), Is.True);
            Assert.That(current, Is.EqualTo(lease));
        }
        finally { canvas.Destroy(true); first.Destroy(true); second.Destroy(true); }
    }

    [Test]
    public void SelectedChildKeepsItsProviderIdentityAcrossIncrementalReparent()
    {
        SceneNode first = new("First parent"), second = new("Second parent");
        SceneNode child = new(first, "Selected child");
        var (canvas, panel) = CreatePanel([first, second]);
        try
        {
            Realize(panel);
            Selection.SceneNodes = [child];
            SceneNode[] selection = Selection.SceneNodes;
            UIVirtualTreeProvider tree = Tree(panel);
            ulong childId = Id(panel, child);
            Assert.That(tree.IsSelected(childId), Is.True);

            child.Transform.SetParent(second.Transform, false, EParentAssignmentMode.Immediate);

            Assert.That(Selection.SceneNodes, Is.SameAs(selection));
            Assert.That(tree.TryGetNode(childId, out UIVirtualTreeNode? item), Is.True);
            Assert.That(item!.ParentId, Is.EqualTo(Id(panel, second)));
            Assert.That(tree.IsSelected(childId), Is.True);
            Assert.That(tree.ActiveId, Is.EqualTo(childId));
        }
        finally { canvas.Destroy(true); first.Destroy(true); second.Destroy(true); }
    }

    [Test]
    public void TransformReplacementObservesTheNewChildListAndRetiresTheOldList()
    {
        SceneNode source = new("Source");
        SceneNode existing = new(source, "Existing child");
        SceneNode stale = new("Old source child");
        var (canvas, panel) = CreatePanel([source]);
        TransformBase previous = source.Transform;
        try
        {
            source.SetTransform<UIBoundableTransform>();
            UIVirtualTreeProvider tree = Tree(panel);
            int count = tree.Count;
            previous.AddChild(stale.Transform, false, EParentAssignmentMode.Immediate);
            Assert.That(tree.Count, Is.EqualTo(count));
            Assert.That(Field<IDictionary>(panel, "_sourceListeners").Contains(stale), Is.False);

            SceneNode added = new(source, "New source child");
            Assert.That(tree.TryGetNode(Id(panel, added), out UIVirtualTreeNode? item), Is.True);
            Assert.That(item!.ParentId, Is.EqualTo(Id(panel, source)));
            Assert.That(tree.TryGetNode(Id(panel, existing), out _), Is.True);
        }
        finally { canvas.Destroy(true); source.Destroy(true); stale.Destroy(true); previous.Destroy(true); }
    }

    [Test]
    public void RootPollingUsesLiveRootsAndTheActualEditorScene()
    {
        using RuntimeWorld world = new(new JitterScene());
        SceneNode first = new("First"), added = new("Added"), editorOnly = new("Editor only");
        world.RootNodes.Add(first);
        EditorWorldIntegration integration = EditorWorldIntegrationRegistry.GetOrAttach(world);
        integration.AddToEditorScene(editorOnly);
        var (canvas, panel) = CreatePanel([first], world);
        try
        {
            Assert.That(Nodes(panel).Values, Does.Not.Contain(editorOnly));
            world.RootNodes.Add(added);
            Poll(panel);
            Assert.That(Nodes(panel).Values, Does.Contain(added));
            world.RootNodes.Remove(first);
            Poll(panel);
            Assert.That(Nodes(panel).Values, Does.Not.Contain(first));

            SetField(panel, "_showEditorScene", true);
            Invoke(panel, "RebuildVirtualTree");
            Assert.That(Nodes(panel).Values, Does.Contain(editorOnly));
            ulong editorId = Id(panel, editorOnly);
            Assert.That(Tree(panel).TryGetNode(editorId, out UIVirtualTreeNode? editorItem), Is.True);
            ulong sceneId = Field<Dictionary<Guid, ulong>>(panel, "_sceneIds")[integration.EditorScene.ID];
            Assert.That(editorItem!.ParentId, Is.EqualTo(sceneId));
            Realize(panel);
            object editorSectionRow = Field<IDictionary>(panel, "_hierarchyRows").Values.Cast<object>()
                .Single(row => ReferenceEquals(Field<XRScene?>(row, "Scene"), integration.EditorScene));
            Assert.That(Field<UIBoundableTransform>(editorSectionRow, "ToggleTransform").Visibility, Is.EqualTo(EVisibility.Hidden));
            Assert.That(Field<UIBoundableTransform>(editorSectionRow, "UnloadTransform").Visibility, Is.EqualTo(EVisibility.Hidden));
            object binding = Field<IDictionary>(panel, "_sourceListeners")[added]!;
            integration.EditorScene.Name = "Editor content";
            integration.EditorScene.MarkDirty();
            Poll(panel);
            Assert.That(Tree(panel).TryGetNode(sceneId, out UIVirtualTreeNode? section), Is.True);
            Assert.That(section!.Name, Is.EqualTo("Editor content *"));
            Assert.That(Field<IDictionary>(panel, "_sourceListeners")[added], Is.SameAs(binding));

            SetField(panel, "_showEditorScene", false);
            Invoke(panel, "RebuildVirtualTree");
            Assert.That(Nodes(panel).Values, Does.Not.Contain(editorOnly));
        }
        finally { canvas.Destroy(true); EditorWorldIntegrationRegistry.Detach(world); first.Destroy(true); }
    }

    [Test]
    public void RenameDraftSurvivesSelectionSourceRefreshAndRowRecycling()
    {
        SceneNode source = new("Original"), other = new("Other");
        var (canvas, panel) = CreatePanel([source, other]);
        try
        {
            UIListTransform list = Realize(panel);
            Invoke(panel, "BeginRename", source);
            UITextInputComponent input = Field<UITextInputComponent>(Row(panel, source), "RenameInput");
            input.Text = "Uncommitted draft";
            source.Name = "External source name";
            Selection.SceneNodes = [other];
            list.SynchronizeVirtualRows(400);
            Invoke(panel, "RebuildVirtualTree");
            list.SynchronizeVirtualRows(400);
            Assert.That(input.Text, Is.EqualTo("Uncommitted draft"));

            list.VirtualOverscan = 0;
            list.SynchronizeVirtualRows(30);
            list.TryScrollToVirtualItem(Id(panel, other));
            list.SynchronizeVirtualRows(30);
            Assert.That(list.TryGetRealizedVirtualRow(Id(panel, source), out _, out _), Is.False);
            list.TryScrollToVirtualItem(Id(panel, source));
            list.SynchronizeVirtualRows(30);
            object rebound = Row(panel, source);
            Assert.That(Field<UITextInputComponent>(rebound, "RenameInput").Text, Is.EqualTo("Uncommitted draft"));
            Invoke(panel, "CancelRename");
            Assert.That(Field<UIBoundableTransform>(rebound, "RenameTransform").Visibility, Is.EqualTo(EVisibility.Hidden));
            Assert.That(source.Name, Is.EqualTo("External source name"));
        }
        finally { canvas.Destroy(true); source.Destroy(true); other.Destroy(true); }
    }

    [Test]
    public void UnchangedRenameSubmitClosesTheEditAndReleasesItsFocus()
    {
        SceneNode source = new("Unchanged");
        var (canvas, panel) = CreatePanel([source]);
        try
        {
            Realize(panel);
            Invoke(panel, "BeginRename", source);
            object row = Row(panel, source);
            UITextInputComponent rename = Field<UITextInputComponent>(row, "RenameInput");
            UITestCanvasInputProbe input = canvas.GetComponent<UITestCanvasInputProbe>()!;
            input.FocusedComponent = rename;

            rename.UserSubmit();

            Assert.That(Field<object?>(panel, "_nodePendingRename"), Is.Null);
            Assert.That(Field<UIBoundableTransform>(row, "RenameTransform").Visibility, Is.EqualTo(EVisibility.Hidden));
            Assert.That(Field<UIBoundableTransform>(row, "LabelTransform").Visibility, Is.EqualTo(EVisibility.Visible));
            Assert.That(input.FocusedComponent, Is.Null);
            Assert.That(source.Name, Is.EqualTo("Unchanged"));
        }
        finally { canvas.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void KeyboardSelectionWaitsForTheDestinationRowPresentation()
    {
        SceneNode first = new("First"), second = new("Second");
        var (root, panel) = CreatePanel([first, second], actualInput: true);
        try
        {
            root.IsActiveSelf = true;
            UIListTransform list = Realize(panel);
            UICanvasComponent canvas = root.GetComponent<UICanvasComponent>()!;
            UICanvasInputComponent input = root.GetComponent<UICanvasInputComponent>()!;
            UIVirtualTreeRowButtonComponent from = Field<UIVirtualTreeRowButtonComponent>(Row(panel, first), "Button");
            UIVirtualTreeRowButtonComponent target = Field<UIVirtualTreeRowButtonComponent>(Row(panel, second), "Button");
            Selection.SceneNodes = [first];
            Assert.That(list.TryGetRealizedVirtualRow(Id(panel, second), out _, out _), Is.False,
                "Selection changes the provider revision; presentation must synchronize its rows.");
            PresentRow(canvas, list, from, 1);
            Assert.That(input.TryFocusPresented(from), Is.True);

            Assert.That(from.NavigateDown(), Is.True);
            Assert.That(Selection.SceneNode, Is.SameAs(first));
            Assert.That(Field<ulong>(panel, "_pendingHierarchyNavigationId"), Is.EqualTo(Id(panel, second)));

            PresentRow(canvas, list, target, 2);
            UIPublication? packet = (UIPublication?)typeof(UICanvasInputComponent)
                .GetMethod("RefreshPresentedInput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(input, null);
            packet?.Release();

            Assert.That(input.FocusedComponent, Is.SameAs(target));
            Assert.That(Selection.SceneNode, Is.SameAs(second));
            Assert.That(Tree(panel).ActiveId, Is.EqualTo(Id(panel, second)));
        }
        finally { root.Destroy(true); first.Destroy(true); second.Destroy(true); }
    }

    [Test]
    public void CancelledNavigationDoesNotSelectOnLaterPointerFocus()
    {
        SceneNode first = new("First"), second = new("Second"), third = new("Third");
        var (root, panel) = CreatePanel([first, second, third], actualInput: true);
        try
        {
            root.IsActiveSelf = true;
            UIListTransform list = Realize(panel);
            UICanvasComponent canvas = root.GetComponent<UICanvasComponent>()!;
            UICanvasInputComponent input = root.GetComponent<UICanvasInputComponent>()!;
            UIVirtualTreeRowButtonComponent from = Field<UIVirtualTreeRowButtonComponent>(Row(panel, first), "Button");
            UIVirtualTreeRowButtonComponent target = Field<UIVirtualTreeRowButtonComponent>(Row(panel, second), "Button");
            Selection.SceneNodes = [first, third];
            SceneNode[] selected = Selection.SceneNodes;
            PresentRow(canvas, list, from, 1);
            Assert.That(input.TryFocusPresented(from), Is.True);
            Assert.That(from.NavigateDown(), Is.True);

            SceneNode outside = new(root, "Other control");
            input.FocusedComponent = outside.AddComponent<UIButtonComponent>()!;
            PresentRow(canvas, list, target, 2);
            Assert.That(input.TryFocusPresented(target), Is.True);

            Assert.That(Selection.SceneNodes, Is.SameAs(selected));
            Assert.That(Field<ulong>(panel, "_pendingHierarchyNavigationId"), Is.Zero);
            Assert.That(Tree(panel).ActiveId, Is.EqualTo(Id(panel, first)));
        }
        finally { root.Destroy(true); first.Destroy(true); second.Destroy(true); third.Destroy(true); }
    }

    [Test]
    public void CameraFocusRequiresTheSamePawnCameraAndWorld()
    {
        using RuntimeWorld world = new(new JitterScene());
        using RuntimeWorld foreignWorld = new(new JitterScene());
        using RuntimeWorldRenderer renderer = new(world, new VisualScene3D());
        using RuntimeWorldRenderer foreignRenderer = new(foreignWorld, new VisualScene3D());
        SceneNode localNode = new("Owned camera") { IsActiveSelf = false };
        SceneNode foreignNode = new("Foreign camera") { IsActiveSelf = false };
        world.RootNodes.Add(localNode);
        foreignWorld.RootNodes.Add(foreignNode);
        CameraComponent local = localNode.AddComponent<CameraComponent>()!;
        CameraComponent foreign = foreignNode.AddComponent<CameraComponent>()!;
        EditorFlyingCameraPawnComponent pawn = localNode.AddComponent<EditorFlyingCameraPawnComponent>()!;
        pawn.CameraComponent = local;
        XRViewport viewport = new(null)
        {
            SetRenderPipelineFromCamera = false,
            CameraComponent = local,
            WorldInstanceOverride = renderer
        };
        try
        {
            Assert.That(HierarchyPanel.IsHierarchyCameraOwner(pawn, viewport, null, world), Is.True);
            viewport.WorldInstanceOverride = foreignRenderer;
            Assert.That(HierarchyPanel.IsHierarchyCameraOwner(pawn, viewport, null, world), Is.False);
            viewport.WorldInstanceOverride = renderer;
            viewport.CameraComponent = foreign;
            Assert.That(HierarchyPanel.IsHierarchyCameraOwner(pawn, viewport, null, world), Is.False);
            viewport.CameraComponent = local;
            pawn.CameraComponent = foreign;
            Assert.That(HierarchyPanel.IsHierarchyCameraOwner(pawn, viewport, null, world), Is.False);
            pawn.CameraComponent = local;
            local.Destroy(true);
            Assert.That(HierarchyPanel.IsHierarchyCameraOwner(pawn, viewport, null, world), Is.False);
        }
        finally { viewport.Destroy(); }
    }

    private static (SceneNode Canvas, HierarchyPanel Panel) CreatePanel(SceneNode[] roots,
        IRuntimeWorldContext? context = null, bool actualInput = false)
    {
        UICanvasTransform transform = new();
        transform.SetSize(new Vector2(400));
        SceneNode canvas = new("Hierarchy workflow", transform) { IsActiveSelf = false };
        canvas.SetWorldContext(context ?? new TestWorldContext());
        canvas.AddComponent<UICanvasComponent>();
        if (actualInput)
            canvas.AddComponent<UICanvasInputComponent>();
        else
            canvas.AddComponent<UITestCanvasInputProbe>();
        SceneNode owner = new(canvas, "Hierarchy");
        HierarchyPanel panel = owner.AddComponent<HierarchyPanel>()!;
        panel.RootNodes = [.. roots];
        Invoke(panel, "OnComponentActivated");
        return (canvas, panel);
    }

    private static UIListTransform Realize(HierarchyPanel panel)
    {
        UIListTransform list = Field<UIListTransform>(panel, "_treeListTransform");
        list.SynchronizeVirtualRows(400);
        return list;
    }

    private static void Poll(HierarchyPanel panel)
    {
        SetField(panel, "_nextSourcePollTicks", 0L);
        Invoke(panel, "PollVirtualTreeSources");
    }

    private static object Row(HierarchyPanel panel, SceneNode node)
        => Field<IDictionary>(panel, "_hierarchyRows").Values.Cast<object>()
            .Single(row => ReferenceEquals(Field<SceneNode?>(row, "Node"), node));
    private static UIVirtualTreeProvider Tree(HierarchyPanel panel) => Field<UIVirtualTreeProvider>(panel, "_virtualTree");
    private static Dictionary<ulong, SceneNode> Nodes(HierarchyPanel panel) => Field<Dictionary<ulong, SceneNode>>(panel, "_hierarchyNodes");
    private static ulong Id(HierarchyPanel panel, SceneNode node) => Field<Dictionary<Guid, ulong>>(panel, "_hierarchyIds")[node.ID];

    private static T Field<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void SetField(object owner, string name, object value)
        => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

    private static void Invoke(HierarchyPanel panel, string name, params object?[] args)
    {
        try { typeof(HierarchyPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }

    private static void PresentRow(UICanvasComponent canvas, UIListTransform list,
        UIVirtualTreeRowButtonComponent button, ulong generation)
    {
        // Match the owner-thread synchronization that precedes real layout capture.
        list.SynchronizeVirtualRows(400);
        UIBoundableTransform row = button.BoundableTransform;
        Assert.That(list.TryGetVirtualRowLease(row, out UIVirtualRowLease lease), Is.True);
        Assert.That(list.TryGetRealizedVirtualRow(lease.ItemId, out UIBoundableTransform? current,
            out UIVirtualRowLease currentLease), Is.True);
        Assert.That(current, Is.SameAs(row));
        Assert.That(currentLease, Is.EqualTo(lease));
        BoundingRectangleF bounds = new(Vector2.Zero, new Vector2(400));
        UIHitRecord hit = new(button.RenderInfo2D, button.RenderInfo2D.MembershipId,
            button.UITransform.ElementIdentity, button.InteractionRevision, Matrix4x4.Identity,
            new Vector2(20), bounds, null, 3, 2, 4, 3, 0, 0, true, false,
            FocusScopeIdentity: canvas.CanvasTransform.ElementIdentity,
            VirtualListIdentity: list.ElementIdentity, VirtualRowIdentity: row.ElementIdentity,
            VirtualRowLease: lease, VirtualProviderIncarnation: list.VirtualProviderIncarnation);
        long revision = canvas.CanvasTransform.ChangeJournal.Revision;
        UIPublication packet = new(generation, canvas.PublicationLifetime, generation, generation,
            revision, default, new UIElementIdentity[]
                { canvas.CanvasTransform.ElementIdentity, list.ElementIdentity, row.ElementIdentity },
            [hit], [], bounds, Matrix4x4.Identity, Matrix4x4.Identity);
        canvas.MarkPresented(canvas, packet);
        packet.Release();
    }
}
