using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using XREngine;
using XREngine.Components;
using XREngine.Data;
using XREngine.Editor;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Rendering;
using XREngine.UnitTests.Scene;

namespace XREngine.UnitTests.Editor;

/// <summary>Checks transient hierarchy values through real dock transfer and save callbacks.</summary>
[TestFixture, NonParallelizable]
public sealed class NativeDockHierarchyTransferTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly ConstructorInfo Constructor = typeof(NativeDockTreeHost).GetConstructor(
        Fields, null, [typeof(SceneNode), typeof(RuntimeWorld), typeof(XRWindow),
            typeof(string), typeof(string), typeof(bool)], null)!;
    private EditorPreferences _priorPreferences = null!;
    private EditorPreferencesOverrides _priorOverrides = null!;
    private IRuntimePlatformPaths? _priorPaths;
    private string? _priorSessionRoot;
    private UITestShaderScope _shaders = null!;
    private RuntimeWorld _world = null!;
    private readonly NativeHierarchyTransferPathProbe _paths = new();
    private readonly List<SceneNode> _hosts = [];
    private readonly List<SceneNode> _authored = [];
    private readonly Guid _section = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _priorPreferences = Engine.GlobalEditorPreferences;
        _priorOverrides = Engine.EditorPreferencesOverrides;
        _priorPaths = RuntimePlatformPaths.Current;
        _priorSessionRoot = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.EditorSessionRoot);
        Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.EditorSessionRoot, null);
        Engine.GlobalEditorPreferences = new EditorPreferences();
        Engine.EditorPreferencesOverrides = new EditorPreferencesOverrides();
        RuntimePlatformPaths.Current = _paths;
        _paths.OnLookup = null;
        _shaders = new();
        _world = new(new HierarchyTestPhysicsScene());
        for (int index = 0; index < 40; index++)
        {
            SceneNode node = new($"Authored {index}");
            _world.RootNodes.Add(node);
            _authored.Add(node);
        }
        _authored[0].NewChild("Child");
    }

    [TearDown]
    public void TearDown()
    {
        _paths.OnLookup = null;
        try
        {
            foreach (SceneNode host in _hosts)
                if (!host.IsDestroyed) host.Destroy(true);
            foreach (SceneNode node in _authored)
                if (!node.IsDestroyed) node.Destroy(true);
            _world.Dispose();
        }
        finally
        {
            _hosts.Clear();
            _authored.Clear();
            _shaders.Dispose();
            Engine.GlobalEditorPreferences = _priorPreferences;
            Engine.EditorPreferencesOverrides = _priorOverrides;
            RuntimePlatformPaths.Current = _priorPaths;
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.EditorSessionRoot, _priorSessionRoot);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TransferUsesLiveExpansionSectionsVisibilityAndEffectiveScroll(bool inactiveDestination)
    {
        var source = CreateHost();
        var destination = CreateHost();
        SetView(Panel(source.Host), true, 120);
        SetView(Panel(destination.Host), false, 30);
        if (inactiveDestination) Deactivate(destination.Host);
        SceneNode[] selection = Selection.SceneNodes;

        Assert.That(Transfer(source, destination), Is.EqualTo(NativeDockTransferResult.Committed));
        HierarchyPanel panel = Panel(destination.Host);
        Assert.That(panel.IsCollapsed(_authored[0]), Is.True);
        Assert.That(Get<HashSet<Guid>>(panel, "_collapsedSections"), Does.Contain(_section));
        Assert.That(Get<bool>(panel, "_showEditorScene"), Is.True);
        Assert.That(List(panel).ContentScrollOffset, Is.EqualTo(120));
        Assert.That(Selection.SceneNodes, Is.SameAs(selection));
        Assert.That(Get<SceneNode?>(panel, "_nodePendingRename"), Is.Null);
        Assert.That(IsOpen(source.Host), Is.True,
            "The registry accepts the destination; the drag owner closes the source afterward.");
    }

    [Test]
    public void SaveFailureRestoresThePriorDestinationViewAndKeepsSourceOpen()
    {
        var source = CreateHost();
        var destination = CreateHost();
        SetView(Panel(source.Host), true, 120);
        SetView(Panel(destination.Host), false, 30);
        Deactivate(destination.Host);
        bool failed = false;
        _paths.OnLookup = () =>
        {
            if (failed || !Get<bool>(Panel(destination.Host), "_showEditorScene")) return;
            failed = true;
            throw new IOException("Injected hierarchy view save failure.");
        };

        Assert.That(Transfer(source, destination), Is.EqualTo(NativeDockTransferResult.Rejected));
        Assert.That(failed, Is.True);
        HierarchyPanel panel = Panel(destination.Host);
        Assert.That(panel.IsCollapsed(_authored[0]), Is.False);
        Assert.That(Get<HashSet<Guid>>(panel, "_collapsedSections"), Is.Empty);
        Assert.That(Get<bool>(panel, "_showEditorScene"), Is.False);
        Assert.That(Get<float>(panel, "_scrollOffset"), Is.EqualTo(30));
        Assert.That(IsOpen(source.Host), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ThrowAfterScrollCommitCompensatesOnlyTheStillOwnedPartialView(bool newerScroll)
    {
        var source = CreateHost();
        var destination = CreateHost();
        SetView(Panel(source.Host), true, 120);
        HierarchyPanel panel = Panel(destination.Host);
        SetView(panel, false, 30);
        Deactivate(destination.Host);
        bool threw = false;
        panel.SceneNode.Activated += _ =>
        {
            UIListTransform list = List(panel);
            list.PropertyChanged += (_, change) =>
            {
                if (threw || change.PropertyName != nameof(UIListTransform.ContentScrollOffset) ||
                    Get<Action?>(panel, "_requireHierarchyViewRestore") is null) return;
                threw = true;
                Assert.That(list.ContentScrollOffset, Is.EqualTo(120));
                if (newerScroll) list.ContentScrollOffset = 180;
                throw new ApplicationException("After the hierarchy scroll committed.");
            };
        };

        Assert.That(Transfer(source, destination), Is.EqualTo(NativeDockTransferResult.Rejected));
        Assert.That(threw, Is.True);
        Assert.That(IsOpen(source.Host), Is.True);
        if (newerScroll)
        {
            Assert.That(List(panel).ContentScrollOffset, Is.EqualTo(180));
            Assert.That(panel.IsCollapsed(_authored[0]), Is.True);
            Assert.That(Get<bool>(panel, "_showEditorScene"), Is.True);
        }
        else
        {
            Assert.That(Get<float>(panel, "_scrollOffset"), Is.EqualTo(30));
            Assert.That(panel.IsCollapsed(_authored[0]), Is.False);
            Assert.That(Get<bool>(panel, "_showEditorScene"), Is.False);
            Assert.That(panel.IsActiveInHierarchy, Is.False,
                "Successful view compensation permits the prior dock selection to return.");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NewerDestinationExpansionDuringSaveCancelsEvenWhenValuesReturn(bool returnToOldValue)
    {
        var source = CreateHost();
        var destination = CreateHost();
        SetView(Panel(source.Host), true, 120);
        bool changed = false;
        _paths.OnLookup = () =>
        {
            HierarchyPanel panel = Panel(destination.Host);
            if (changed || !Get<bool>(panel, "_showEditorScene")) return;
            changed = true;
            panel.ToggleCollapse(_authored[0]);
            if (returnToOldValue) panel.ToggleCollapse(_authored[0]);
        };

        Assert.That(Transfer(source, destination), Is.EqualTo(NativeDockTransferResult.Rejected));
        Assert.That(changed, Is.True);
        Assert.That(Panel(destination.Host).IsCollapsed(_authored[0]), Is.EqualTo(returnToOldValue));
        Assert.That(Get<bool>(Panel(destination.Host), "_showEditorScene"), Is.True);
        Assert.That(IsOpen(source.Host), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NewerSourceIntentDuringSavePreventsSourceClosure(bool scroll)
    {
        var source = CreateHost();
        var destination = CreateHost();
        HierarchyPanel sourcePanel = Panel(source.Host);
        SetView(sourcePanel, true, 120);
        bool changed = false;
        _paths.OnLookup = () =>
        {
            if (changed || !Get<bool>(Panel(destination.Host), "_showEditorScene")) return;
            changed = true;
            if (scroll)
                List(sourcePanel).ContentScrollOffset = List(sourcePanel).ContentScrollOffset;
            else
            {
                sourcePanel.ToggleCollapse(_authored[0]);
                sourcePanel.ToggleCollapse(_authored[0]);
            }
        };

        Assert.That(Transfer(source, destination), Is.EqualTo(NativeDockTransferResult.Rejected));
        Assert.That(changed, Is.True);
        Assert.That(sourcePanel.IsCollapsed(_authored[0]), Is.True);
        Assert.That(IsOpen(source.Host), Is.True);
    }

    [Test]
    public void TransferredDestinationPanelIsPreservedAndDoesNotCloseSource()
    {
        var source = CreateHost();
        var destination = CreateHost();
        SetView(Panel(source.Host), true, 120);
        HierarchyPanel destinationPanel = Panel(destination.Host);
        SceneNode foreign = NewHostRoot();
        bool moved = false;
        _paths.OnLookup = () =>
        {
            if (moved || !Get<bool>(destinationPanel, "_showEditorScene")) return;
            moved = true;
            destinationPanel.Transform.SetParent(foreign.Transform, false, EParentAssignmentMode.Immediate);
        };

        Assert.That(Transfer(source, destination), Is.EqualTo(NativeDockTransferResult.Rejected));
        Assert.That(moved, Is.True);
        Assert.That(destinationPanel.SceneNode.Parent, Is.SameAs(foreign));
        Assert.That(destinationPanel.IsDestroyed, Is.False);
        Assert.That(IsOpen(source.Host), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ScrollWriteRejectsSourceChangeOrTransferredListBeforeTheOldOffsetIsStored(bool transferList)
    {
        var source = CreateHost();
        var destination = CreateHost();
        HierarchyPanel sourcePanel = Panel(source.Host);
        HierarchyPanel destinationPanel = Panel(destination.Host);
        SetView(sourcePanel, true, 120);
        SceneNode destinationSlot = Slots(destination.Host)[NativeDockPanel.Hierarchy];
        Assert.That(sourcePanel.TryCaptureHierarchyView(Slots(source.Host)[NativeDockPanel.Hierarchy],
            source.Root, false, out var receipt), Is.True);
        UIListTransform list = List(destinationPanel);
        float previous = list.ContentScrollOffset;
        SceneNode foreign = NewHostRoot();
        bool changed = false;
        list.PropertyChanging += (_, change) =>
        {
            if (changed || change.PropertyName != nameof(UIListTransform.ContentScrollOffset)) return;
            changed = true;
            if (transferList) list.SetParent(foreign.Transform, false, EParentAssignmentMode.Immediate);
            else sourcePanel.ToggleCollapse(_authored[0]);
        };

        Assert.Throws<InvalidOperationException>(() => destinationPanel.RestoreHierarchyView(
            receipt!.Values, destinationSlot, destination.Root, () => receipt.IsCurrent));
        Assert.That(changed, Is.True);
        Assert.That(list.ContentScrollOffset, Is.EqualTo(previous));
        if (transferList) Assert.That(list.Parent, Is.SameAs(foreign.Transform));
    }

    [Test]
    public void CapturedViewRejectsRegeneratedPanelTransform()
    {
        var source = CreateHost();
        HierarchyPanel panel = Panel(source.Host);
        Assert.That(panel.TryCaptureHierarchyView(Slots(source.Host)[NativeDockPanel.Hierarchy],
            source.Root, false, out var receipt), Is.True);
        panel.Transform.Generate();
        Assert.That(receipt!.IsCurrent, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ThumbTransferDuringGeometryWritePreservesTheNewOwner(bool height)
    {
        var source = CreateHost();
        var destination = CreateHost();
        HierarchyPanel sourcePanel = Panel(source.Host);
        HierarchyPanel destinationPanel = Panel(destination.Host);
        SetView(sourcePanel, true, 120);
        Assert.That(sourcePanel.TryCaptureHierarchyView(Slots(source.Host)[NativeDockPanel.Hierarchy],
            source.Root, false, out var receipt), Is.True);
        UIBoundableTransform thumb = Get<UIBoundableTransform>(destinationPanel, "_scrollbarThumbTransform");
        float? oldHeight = thumb.Height;
        var oldTranslation = thumb.Translation;
        SceneNode foreign = NewHostRoot();
        bool moved = false;
        thumb.PropertyChanging += (_, change) =>
        {
            if (moved || change.PropertyName != (height ? nameof(UIBoundableTransform.Height) : nameof(UITransform.Translation)))
                return;
            moved = true;
            thumb.SetParent(foreign.Transform, false, EParentAssignmentMode.Immediate);
        };
        Assert.Throws<InvalidOperationException>(() => destinationPanel.RestoreHierarchyView(
            receipt!.Values, Slots(destination.Host)[NativeDockPanel.Hierarchy], destination.Root,
            () => receipt.IsCurrent));
        Assert.That(moved, Is.True);
        Assert.That(thumb.Parent, Is.SameAs(foreign.Transform));
        if (height) Assert.That(thumb.Height, Is.EqualTo(oldHeight));
        else Assert.That(thumb.Translation, Is.EqualTo(oldTranslation));
    }

    private (SceneNode Root, NativeDockTreeHost Host) CreateHost()
    {
        string key = "hierarchy-transfer-" + Guid.NewGuid().ToString("N");
        Dictionary<string, NativeDockLayout> layouts = JsonSerializer.Deserialize<Dictionary<string, NativeDockLayout>>(
            string.IsNullOrEmpty(Engine.GlobalEditorPreferences.NativeDockLayoutsJson)
                ? "{}" : Engine.GlobalEditorPreferences.NativeDockLayoutsJson)!;
        layouts[key] = new NativeDockLayout
        {
            DockTree = new NativeDockTree
            {
                Root = new NativeDockNode
                {
                    Tabs = [NativeDockPanel.Hierarchy, NativeDockPanel.Console],
                    Selected = NativeDockPanel.Hierarchy,
                },
            },
        }.Sanitize();
        Engine.GlobalEditorPreferences.NativeDockLayoutsJson = JsonSerializer.Serialize(layouts);
        SceneNode root = NewHostRoot();
        var host = (NativeDockTreeHost)Constructor.Invoke(BindingFlags.DoNotWrapExceptions, null,
            [root, _world, null, key, null, false], null);
        return (root, host);
    }

    private SceneNode NewHostRoot()
    {
        SceneNode root = new("Hierarchy transfer host", new UICanvasTransform());
        root.AddComponent<UICanvasComponent>();
        root.SetWorldContext(_world);
        _hosts.Add(root);
        return root;
    }

    private void SetView(HierarchyPanel panel, bool source, float offset)
    {
        HashSet<Guid> nodes = Get<HashSet<Guid>>(panel, "_collapsedNodes");
        HashSet<Guid> sections = Get<HashSet<Guid>>(panel, "_collapsedSections");
        nodes.Clear();
        sections.Clear();
        if (source) { nodes.Add(_authored[0].ID); sections.Add(_section); }
        typeof(HierarchyPanel).GetField("_showEditorScene", Fields)!.SetValue(panel, source);
        panel.RemakeChildren();
        // The list owns the effective offset; the panel's cached offset deliberately differs.
        List(panel).ContentScrollOffset = offset;
    }

    private static NativeDockTransferResult Transfer(
        (SceneNode Root, NativeDockTreeHost Host) source,
        (SceneNode Root, NativeDockTreeHost Host) destination)
        => NativeDockWindowRegistry.TransferToWindow(source.Root.GetComponent<NativeDockWindowRegistration>()!,
            destination.Root.GetComponent<NativeDockWindowRegistration>()!, NativeDockPanel.Hierarchy,
            Get<NativeDockTree>(destination.Host, "_tree").Stacks().First().Id);

    private static void Deactivate(NativeDockTreeHost host)
    {
        Get<NativeDockTree>(host, "_tree").Stacks().First().Selected = NativeDockPanel.Console;
        typeof(NativeDockTreeHost).GetMethod("Render", Fields)!.Invoke(host,
            BindingFlags.DoNotWrapExceptions, null, null, null);
        Assert.That(Panel(host).IsActiveInHierarchy, Is.False);
    }

    private static Dictionary<string, SceneNode> Slots(NativeDockTreeHost host)
        => Get<Dictionary<string, SceneNode>>(host, "_panelSlots");

    private static HierarchyPanel Panel(NativeDockTreeHost host)
        => Slots(host)[NativeDockPanel.Hierarchy].Transform.Children.Select(child => child.SceneNode!)
            .Select(node => node.GetComponent<HierarchyPanel>()).First(panel => panel is not null)!;

    private static UIListTransform List(HierarchyPanel panel) => Get<UIListTransform>(panel, "_treeListTransform");
    private static bool IsOpen(NativeDockTreeHost host)
        => Get<NativeDockTree>(host, "_tree").Stacks().Any(stack => stack.Tabs.Contains(NativeDockPanel.Hierarchy));
    private static T Get<T>(object instance, string field)
        => (T)instance.GetType().GetField(field, Fields)!.GetValue(instance)!;
}
