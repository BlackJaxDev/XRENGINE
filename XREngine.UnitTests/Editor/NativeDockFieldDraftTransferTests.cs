using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using XREngine;
using XREngine.Components;
using XREngine.Data;
using XREngine.Editor;
using XREngine.Editor.UI.Components;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Rendering;
using XREngine.UnitTests.Scene;

namespace XREngine.UnitTests.Editor;

/// <summary>Checks that unsubmitted Console and Assets field drafts move with a real dock transfer.</summary>
[TestFixture, NonParallelizable]
public sealed class NativeDockFieldDraftTransferTests
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
    }

    [TearDown]
    public void TearDown()
    {
        _paths.OnLookup = null;
        try
        {
            foreach (SceneNode host in _hosts)
                if (!host.IsDestroyed) host.Destroy(true);
            _world.Dispose();
        }
        finally
        {
            _hosts.Clear();
            _shaders.Dispose();
            Engine.GlobalEditorPreferences = _priorPreferences;
            Engine.EditorPreferencesOverrides = _priorOverrides;
            RuntimePlatformPaths.Current = _priorPaths;
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.EditorSessionRoot, _priorSessionRoot);
        }
    }

    [Test]
    public void ConsoleFilterDraftAndCaretMoveWithTheTransfer()
    {
        var destination = CreateHost(NativeDockPanel.Assets);
        var source = CreateHost(NativeDockPanel.Console);
        ConsolePanel sourcePanel = Find<ConsolePanel>(source.Host, NativeDockPanel.Console);
        sourcePanel.Filter = "committed";
        UITextInputComponent sourceInput = Get<UITextInputComponent>(sourcePanel, "_filterInput");
        sourceInput.Text = "unsubmitted";
        sourceInput.CursorPosition = 5;
        sourceInput.SelectionAnchor = 2;

        Assert.That(Transfer(source, destination, NativeDockPanel.Console), Is.EqualTo(NativeDockTransferResult.Committed));

        ConsolePanel received = Find<ConsolePanel>(destination.Host, NativeDockPanel.Console);
        UITextInputComponent input = Get<UITextInputComponent>(received, "_filterInput");
        Assert.That(received.Filter, Is.EqualTo("committed"));
        Assert.That((input.Text, input.CursorPosition, input.SelectionAnchor), Is.EqualTo(("unsubmitted", 5, 2)));
    }

    [Test]
    public void AssetSearchDraftMovesWithTheTransfer()
    {
        var destination = CreateHost(NativeDockPanel.Console);
        var source = CreateHost(NativeDockPanel.Assets);
        AssetsPanel sourcePanel = Find<AssetsPanel>(source.Host, NativeDockPanel.Assets);
        sourcePanel.Search = "committed";
        UITextInputComponent sourceInput = Get<UITextInputComponent>(sourcePanel, "_searchInput");
        sourceInput.Text = "draft";
        sourceInput.CursorPosition = 3;
        sourceInput.SelectionAnchor = 3;

        Assert.That(Transfer(source, destination, NativeDockPanel.Assets), Is.EqualTo(NativeDockTransferResult.Committed));

        AssetsPanel received = Find<AssetsPanel>(destination.Host, NativeDockPanel.Assets);
        UITextInputComponent input = Get<UITextInputComponent>(received, "_searchInput");
        Assert.That(received.Search, Is.EqualTo("committed"));
        Assert.That((input.Text, input.CursorPosition, input.SelectionAnchor), Is.EqualTo(("draft", 3, 3)));
    }

    [Test]
    public void UnchangedDraftDoesNotWriteTheDestinationField()
    {
        var destination = CreateHost(NativeDockPanel.Assets);
        var source = CreateHost(NativeDockPanel.Console);
        ConsolePanel sourcePanel = Find<ConsolePanel>(source.Host, NativeDockPanel.Console);
        sourcePanel.Filter = "same";

        Assert.That(Transfer(source, destination, NativeDockPanel.Console), Is.EqualTo(NativeDockTransferResult.Committed));

        ConsolePanel received = Find<ConsolePanel>(destination.Host, NativeDockPanel.Console);
        UITextInputComponent input = Get<UITextInputComponent>(received, "_filterInput");
        Assert.That(input.Text, Is.EqualTo("same"));
        Assert.That(input.IsFocused, Is.False);
    }

    private (SceneNode Root, NativeDockTreeHost Host) CreateHost(string panel)
    {
        string key = "draft-transfer-" + Guid.NewGuid().ToString("N");
        Dictionary<string, NativeDockLayout> layouts = JsonSerializer.Deserialize<Dictionary<string, NativeDockLayout>>(
            string.IsNullOrEmpty(Engine.GlobalEditorPreferences.NativeDockLayoutsJson)
                ? "{}" : Engine.GlobalEditorPreferences.NativeDockLayoutsJson)!;
        layouts[key] = new NativeDockLayout
        {
            DockTree = new NativeDockTree
            {
                Root = new NativeDockNode { Tabs = [panel], Selected = panel },
            },
        }.Sanitize();
        Engine.GlobalEditorPreferences.NativeDockLayoutsJson = JsonSerializer.Serialize(layouts);
        SceneNode root = new("Draft transfer host", new UICanvasTransform());
        root.AddComponent<UICanvasComponent>();
        root.SetWorldContext(_world);
        _hosts.Add(root);
        var host = (NativeDockTreeHost)Constructor.Invoke(BindingFlags.DoNotWrapExceptions, null,
            [root, _world, null, key, null, false], null);
        return (root, host);
    }

    private static NativeDockTransferResult Transfer(
        (SceneNode Root, NativeDockTreeHost Host) source,
        (SceneNode Root, NativeDockTreeHost Host) destination, string panel)
        => NativeDockWindowRegistry.TransferToWindow(source.Root.GetComponent<NativeDockWindowRegistration>()!,
            destination.Root.GetComponent<NativeDockWindowRegistration>()!, panel,
            Get<NativeDockTree>(destination.Host, "_tree").Stacks().First().Id);

    private static T Find<T>(NativeDockTreeHost host, string panel) where T : XRComponent
        => Get<Dictionary<string, SceneNode>>(host, "_panelSlots")[panel].Transform.Children
            .Select(child => child.SceneNode!.GetComponent<T>()).First(found => found is not null)!;

    private static T Get<T>(object instance, string field)
    {
        for (Type? type = instance.GetType(); type is not null; type = type.BaseType)
            if (type.GetField(field, Fields) is { } info)
                return (T)info.GetValue(instance)!;
        throw new MissingFieldException(instance.GetType().Name, field);
    }
}
