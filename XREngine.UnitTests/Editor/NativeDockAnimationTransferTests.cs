using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using XREngine;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Rendering;
using XREngine.UnitTests.Scene;

namespace XREngine.UnitTests.Editor;

/// <summary>Checks Animation view transfer and compensation through real dock hosts.</summary>
[TestFixture, NonParallelizable]
public sealed partial class NativeDockAnimationTransferTests
{
    private static readonly BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly ConstructorInfo HostConstructor =
        typeof(NativeDockTreeHost).GetConstructor(InstanceFlags, null,
            [typeof(SceneNode), typeof(RuntimeWorld), typeof(XRWindow), typeof(string),
                typeof(string), typeof(bool)], null)
        ?? throw new InvalidOperationException("The native dock host constructor is unavailable.");
    private static readonly FieldInfo PanelSlotsField = typeof(NativeDockTreeHost)
        .GetField("_panelSlots", InstanceFlags)
        ?? throw new InvalidOperationException("The native dock panel slots are unavailable.");
    private static readonly FieldInfo TreeField = typeof(NativeDockTreeHost)
        .GetField("_tree", InstanceFlags)
        ?? throw new InvalidOperationException("The native dock tree is unavailable.");
    private static readonly MethodInfo RenderMethod = typeof(NativeDockTreeHost)
        .GetMethod("Render", InstanceFlags)
        ?? throw new InvalidOperationException("The native dock render method is unavailable.");
    private static readonly PropertyInfo SelectedTargetProperty = typeof(NativeAssetSelection)
        .GetProperty(nameof(NativeAssetSelection.SelectedTarget))
        ?? throw new InvalidOperationException("The native asset selection property is unavailable.");
    private static readonly FieldInfo DisplayedClipField = typeof(AnimationPanel)
        .GetField("_displayedClip", InstanceFlags)!;
    private static readonly FieldInfo PlotZoomField = typeof(AnimationPanel)
        .GetField("_plotZoom", InstanceFlags)!;
    private static readonly FieldInfo TimeInputField = typeof(AnimationPanel)
        .GetField("_timeInput", InstanceFlags)!;

    [Test]
    public void InactiveDestinationReceivesSourceClipWithoutChangingGlobalSelection()
    {
        using var fixture = new DockFixture();
        AnimationClip clipA = NewClip("A");
        AnimationClip clipB = NewClip("B");
        fixture.InstallLayouts();

        Host destination = fixture.CreateHost(fixture.World, fixture.DestinationKey,
            clipA, plotZoom: 1.5f);
        destination.DeactivateAnimationTab(NativeDockPanel.Console);
        SetSelectedTarget(clipB);
        Host source = fixture.CreateHost(fixture.World, fixture.SourceKey,
            clipB, plotZoom: 2.0f);

        SetPathSearch(source.Animation, "source query");
        SetPathSearch(destination.Animation, "destination query");

        NativeDockTransferResult result = Transfer(source, destination);

        Assert.That(result, Is.EqualTo(NativeDockTransferResult.Committed));
        Assert.That(NativeAssetSelection.SelectedTarget, Is.SameAs(clipB));
        Assert.That(DisplayedClip(destination.Animation), Is.SameAs(clipB));
        Assert.That(PlotZoom(destination.Animation), Is.EqualTo(2.0f));
        Assert.That(PathSearch(destination.Animation), Is.EqualTo("source query"));
        Assert.That(SearchInput(destination.Animation).Text, Is.EqualTo("source query"));
    }

    [Test]
    public void SaveFailureAfterDifferentClipRestoreDoesNotCompensateOrChangeGlobalSelection()
    {
        using var fixture = new DockFixture(failOnPathLookup: 2);
        AnimationClip clipA = NewClip("A");
        AnimationClip clipB = NewClip("B");
        fixture.InstallLayouts();

        Host destination = fixture.CreateHost(fixture.World, fixture.DestinationKey,
            clipA, plotZoom: 1.5f);
        destination.DeactivateAnimationTab(NativeDockPanel.Console);
        SetSelectedTarget(clipB);
        Host source = fixture.CreateHost(fixture.World, fixture.SourceKey,
            clipB, plotZoom: 2.0f);

        NativeDockTransferResult result = Transfer(source, destination);

        Assert.That(result, Is.EqualTo(NativeDockTransferResult.Rejected));
        Assert.That(fixture.Paths.LookupCount, Is.EqualTo(2),
            "The first lookup saves the moved tree; the second fails after view restore.");
        Assert.That(NativeAssetSelection.SelectedTarget, Is.SameAs(clipB));
        Assert.That(DisplayedClip(destination.Animation), Is.SameAs(clipB));
        Assert.That(PlotZoom(destination.Animation), Is.EqualTo(2.0f),
            "A prior view for clip A must not be restored over the current clip B.");
        Assert.That(IsPanelOpen(source.Tree, NativeDockPanel.Animation), Is.True,
            "A rejected destination receipt must leave the source panel open.");
    }

    [Test]
    public void SaveFailureForSameClipRestoresThePriorLocalView()
    {
        using var fixture = new DockFixture(failOnPathLookup: 2);
        AnimationClip clip = NewClip("Shared");
        fixture.InstallLayouts();

        Host destination = fixture.CreateHost(fixture.World, fixture.DestinationKey,
            clip, plotZoom: 1.5f);
        destination.DeactivateAnimationTab(NativeDockPanel.Console);
        SetSelectedTarget(clip);
        Host source = fixture.CreateHost(fixture.World, fixture.SourceKey,
            clip, plotZoom: 2.0f);

        SetPathSearch(source.Animation, "source query");
        SetPathSearch(destination.Animation, "destination query");
        NativeDockTransferResult result = Transfer(source, destination);

        Assert.That(result, Is.EqualTo(NativeDockTransferResult.Rejected));
        Assert.That(fixture.Paths.LookupCount, Is.EqualTo(4),
            "The failed receive save is followed by the compensation and tree-restore saves.");
        Assert.That(NativeAssetSelection.SelectedTarget, Is.SameAs(clip));
        Assert.That(DisplayedClip(destination.Animation), Is.SameAs(clip));
        Assert.That(PlotZoom(destination.Animation), Is.EqualTo(1.5f),
            "A same-clip failure must restore the destination's prior local view.");
        Assert.That(IsPanelOpen(source.Tree, NativeDockPanel.Animation), Is.True);
        Assert.That(PathSearch(destination.Animation), Is.EqualTo("destination query"));
        Assert.That(SearchInput(destination.Animation).Text, Is.EqualTo("destination query"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SaveCallbackTextMutationInvalidatesAppliedReceiptAndKeepsSourceOpen(bool search)
    {
        using var fixture = new DockFixture();
        AnimationClip clip = NewClip("Shared");
        fixture.InstallLayouts();

        Host destination = fixture.CreateHost(fixture.World, fixture.DestinationKey,
            clip, plotZoom: 1.5f);
        destination.DeactivateAnimationTab(NativeDockPanel.Console);
        SetSelectedTarget(clip);
        Host source = fixture.CreateHost(fixture.World, fixture.SourceKey,
            clip, plotZoom: 2.0f);
        UITextInputComponent timeInput = search ? SearchInput(destination.Animation) :
            (UITextInputComponent)TimeInputField.GetValue(destination.Animation)!;
        bool edited = false;
        fixture.Paths.OnLookup = lookup =>
        {
            if (lookup != 2)
                return;
            timeInput.Text = "9";
            edited = true;
        };
        NativeDockTransferResult result = Transfer(source, destination);

        Assert.That(result, Is.EqualTo(NativeDockTransferResult.Rejected));
        Assert.That(edited, Is.True,
            "The path callback must run during the save after Animation view restore.");
        Assert.That(fixture.Paths.LookupCount, Is.EqualTo(2));
        Assert.That(PlotZoom(destination.Animation), Is.EqualTo(2.0f),
            "The source view must be applied before the save callback edits the toolbar.");
        Assert.That(timeInput.Text, Is.EqualTo("9"));
        Assert.That(NativeAssetSelection.SelectedTarget, Is.SameAs(clip));
        Assert.That(IsPanelOpen(source.Tree, NativeDockPanel.Animation), Is.True,
            "A stale applied receipt must not authorize source closure.");
    }

    private static string PathSearch(AnimationPanel panel)
        => (string)typeof(AnimationPanel).GetField("_pathSearch", InstanceFlags)!.GetValue(panel)!;

    private static UITextInputComponent SearchInput(AnimationPanel panel)
        => (UITextInputComponent)typeof(AnimationPanel).GetField("_searchInput", InstanceFlags)!.GetValue(panel)!;

    private static void SetPathSearch(AnimationPanel panel, string query)
    {
        typeof(AnimationPanel).GetField("_pathSearch", InstanceFlags)!.SetValue(panel, query);
        SearchInput(panel).Text = query;
    }

    private static NativeDockTransferResult Transfer(Host source, Host destination)
    {
        NativeDockNode stack = destination.Tree.Stacks().First(item =>
            item.Tabs.Contains(NativeDockPanel.Animation));
        return NativeDockWindowRegistry.TransferToWindow(source.Registration,
            destination.Registration, NativeDockPanel.Animation, stack.Id);
    }

    private static bool IsPanelOpen(NativeDockTree tree, string panel)
        => tree.Stacks().Any(stack => stack.Tabs.Contains(panel));

    private static AnimationClip NewClip(string name)
        => new() { Name = name, LengthInSeconds = 2 };

    private static void SetSelectedTarget(object? value)
        => SelectedTargetProperty.SetValue(null, value);

    private static AnimationClip? DisplayedClip(AnimationPanel panel)
        => (AnimationClip?)DisplayedClipField.GetValue(panel);

    private static float PlotZoom(AnimationPanel panel)
        => (float)PlotZoomField.GetValue(panel)!;

    private sealed class Host(SceneNode root, object host)
    {
        internal SceneNode Root { get; } = root;
        internal object Instance { get; } = host;
        internal NativeDockWindowRegistration Registration =>
            Root.GetComponent<NativeDockWindowRegistration>()!;
        internal NativeDockTree Tree => (NativeDockTree)TreeField.GetValue(Instance)!;
        internal AnimationPanel Animation
        {
            get
            {
                Dictionary<string, SceneNode> slots =
                    (Dictionary<string, SceneNode>)PanelSlotsField.GetValue(Instance)!;
                SceneNode panelNode = slots[NativeDockPanel.Animation].Transform.Children
                    .Select(child => child.SceneNode!)
                    .First(node => node.GetComponent<AnimationPanel>() is not null);
                return panelNode.GetComponent<AnimationPanel>()!;
            }
        }

        internal void DeactivateAnimationTab(string nextPanel)
        {
            NativeDockNode stack = Tree.Stacks().First(item =>
                item.Tabs.Contains(NativeDockPanel.Animation));
            stack.Selected = nextPanel;
            RenderMethod.Invoke(Instance, null);
            Assert.That(Animation.IsActiveInHierarchy, Is.False);
        }
    }

    private sealed class DockFixture : IDisposable
    {
        private static readonly string SessionRootVariable =
            XREngineEnvironmentVariables.EditorSessionRoot;
        private readonly EditorPreferences _priorGlobal = Engine.GlobalEditorPreferences;
        private readonly EditorPreferencesOverrides _priorOverrides = Engine.EditorPreferencesOverrides;
        private readonly IRuntimePlatformPaths? _priorPaths = RuntimePlatformPaths.Current;
        private readonly string? _priorSessionRoot = Environment.GetEnvironmentVariable(SessionRootVariable);
        private readonly object? _priorSelection = NativeAssetSelection.SelectedTarget;
        private readonly List<SceneNode> _roots = [];
        private readonly UITestShaderScope _shaders = new();

        internal DockFixture(int failOnPathLookup = 0)
        {
            Environment.SetEnvironmentVariable(SessionRootVariable, null);
            Engine.GlobalEditorPreferences = new EditorPreferences();
            Engine.EditorPreferencesOverrides = new EditorPreferencesOverrides();
            Paths.FailOnLookup = failOnPathLookup;
            RuntimePlatformPaths.Current = Paths;
        }

        internal string SourceKey { get; } = "native-dock-source-" + Guid.NewGuid().ToString("N");
        internal string DestinationKey { get; } = "native-dock-destination-" + Guid.NewGuid().ToString("N");
        internal PathProbe Paths { get; } = new();
        internal RuntimeWorld World { get; } = new(new HierarchyTestPhysicsScene());

        internal void InstallLayouts()
        {
            Dictionary<string, NativeDockLayout> layouts = new(StringComparer.Ordinal)
            {
                [SourceKey] = Layout(NativeDockPanel.Animation),
                [DestinationKey] = Layout(NativeDockPanel.Animation),
            };
            Engine.GlobalEditorPreferences.NativeDockLayoutsJson =
                JsonSerializer.Serialize(layouts);
        }

        internal Host CreateHost(RuntimeWorld world, string key, AnimationClip clip,
            float plotZoom)
        {
            SetSelectedTarget(clip);
            SceneNode root = new("Dock " + key, new UICanvasTransform());
            root.AddComponent<UICanvasComponent>();
            root.SetWorldContext(world);
            _roots.Add(root);
            object instance;
            try
            {
                instance = HostConstructor.Invoke([root, world, null, key, null, false]);
            }
            catch
            {
                root.Destroy(true);
                throw;
            }
            Host host = new(root, instance);
            PlotZoomField.SetValue(host.Animation, plotZoom);
            return host;
        }

        public void Dispose()
        {
            try
            {
                foreach (SceneNode root in _roots)
                    if (!root.IsDestroyed)
                        root.Destroy(true);
            }
            finally
            {
                try { World.Dispose(); }
                finally
                {
                    try { _shaders.Dispose(); }
                    finally
                    {
                        try { SetSelectedTarget(_priorSelection); }
                        finally
                        {
                            try { Engine.GlobalEditorPreferences = _priorGlobal; }
                            finally
                            {
                                try { Engine.EditorPreferencesOverrides = _priorOverrides; }
                                finally
                                {
                                    try { RuntimePlatformPaths.Current = _priorPaths; }
                                    finally
                                    {
                                        Environment.SetEnvironmentVariable(
                                            SessionRootVariable, _priorSessionRoot);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private static NativeDockLayout Layout(string selectedPanel)
            => new NativeDockLayout()
            {
                DockTree = new NativeDockTree
                {
                    Root = new NativeDockNode
                    {
                        Tabs = [NativeDockPanel.Assets, NativeDockPanel.Console,
                            NativeDockPanel.Profiler, NativeDockPanel.Animation],
                        Selected = selectedPanel,
                    },
                },
            }.Sanitize();
    }

    private sealed class PathProbe : IRuntimePlatformPaths
    {
        private int _lookupCount;
        internal int LookupCount => Volatile.Read(ref _lookupCount);
        internal int FailOnLookup { get; set; }
        internal Action<int>? OnLookup { get; set; }

        public string GetFolderPath(Environment.SpecialFolder folder)
        {
            int lookup = Interlocked.Increment(ref _lookupCount);
            OnLookup?.Invoke(lookup);
            if (lookup == FailOnLookup)
                throw new IOException("Injected preference-save failure.");
            return string.Empty;
        }
    }
}
