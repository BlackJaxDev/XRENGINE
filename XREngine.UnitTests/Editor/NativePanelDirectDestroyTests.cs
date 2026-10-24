using System.Reflection;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public class NativePanelDirectDestroyTests
{
    [Test]
    public void AnimationPanel_DirectDestroy_RemovesSelectionCallbacksWithoutDestroyingChildren()
    {
        SceneNode node = NewPanelNode();
        SceneNode child = new(node, "Retained child");
        AnimationPanel panel = node.AddComponent<AnimationPanel>()!;
        MethodInfo selection = PrivateMethod(panel, "SelectionChanged");
        MethodInfo asset = PrivateMethod(panel, "AssetSelectionChanged");
        Action<SceneNode[]> selectionHandler = (Action<SceneNode[]>)Delegate.CreateDelegate(typeof(Action<SceneNode[]>), panel, selection);
        Action<object?> assetHandler = (Action<object?>)Delegate.CreateDelegate(typeof(Action<object?>), panel, asset);
        Selection.SelectionChanged += selectionHandler;
        NativeAssetSelection.SelectionChanged += assetHandler;
        SetPrivateField(panel, "_ownerSubscriptionsActive", true);
        try
        {
            panel.Destroy(true);
            Assert.Multiple(() =>
            {
                Assert.That(HasHandler(typeof(Selection), "SelectionChanged", panel), Is.False);
                Assert.That(HasHandler(typeof(NativeAssetSelection), "SelectionChanged", panel), Is.False);
                Assert.That(child.IsDestroyed, Is.False);
                Assert.That(child.Parent, Is.SameAs(node));
            });
        }
        finally
        {
            Selection.SelectionChanged -= selectionHandler;
            NativeAssetSelection.SelectionChanged -= assetHandler;
            node.Destroy(true);
        }
    }

    [Test]
    public void AssetsPanel_DirectDestroy_DisposesDropRegistrationAndDetachesVirtualRowCallbacks()
    {
        SceneNode node = NewPanelNode();
        SceneNode child = new(node, "Retained child");
        AssetsPanel panel = node.AddComponent<AssetsPanel>()!;
        MemoryStream drop = new();
        SetPrivateField(panel, "_folderDropTarget", drop);
        PrivateMethod(panel, "AttachTableCallbacks").Invoke(panel, null);
        object table = GetPrivateField(panel, "_table")!;
        try
        {
            Assert.That(HasHandler(table.GetType(), "Activated", panel, table), Is.True);
            panel.Destroy(true);
            Assert.Multiple(() =>
            {
                Assert.That(drop.CanRead, Is.False);
                Assert.That(HasHandler(table.GetType(), "Activated", panel, table), Is.False);
                Assert.That(HasHandler(table.GetType(), "RowPointerMoved", panel, table), Is.False);
                Assert.That(child.IsDestroyed, Is.False);
            });
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void InspectorPanel_DirectDestroy_StopsRestoreAndDetachesObservedTargetsWithoutChildTeardown()
    {
        SceneNode node = NewPanelNode();
        SceneNode child = new(node, "Retained detail");
        SceneNode observed = new("Observed component owner");
        InspectorPanel panel = node.AddComponent<InspectorPanel>()!;
        SceneNode listNode = new(node, "Virtual properties");
        UIListTransform list = listNode.SetTransform<UIListTransform>();
        list.ItemSize = 10;
        UIVirtualTreeProvider provider = new(() => new UIBoundableTransform(),
            (_, _, _, _) => { }, (_, _, _) => { });
        provider.Insert(1, 0, 0, "Property");
        list.VirtualProvider = provider;
        list.SynchronizeVirtualRows(10);
        UIBoundableTransform retainedRow = (UIBoundableTransform)list.Children[0];
        SetPrivateField(panel, "_virtualPropertyList", list);
        SetPrivateField(panel, "_pendingFocusFrames", 10);
        PrivateMethod(panel, "ObserveComponentNodes").Invoke(panel, [new SceneNode[] { observed }]);
        Action<SceneNode[]> handler = (Action<SceneNode[]>)Delegate.CreateDelegate(
            typeof(Action<SceneNode[]>), panel, PrivateMethod(panel, "OnSelectionChanged"));
        Selection.SelectionChanged += handler;
        try
        {
            panel.Destroy(true);
            Assert.Multiple(() =>
            {
                Assert.That(GetPrivateField(panel, "_pendingFocusFrames"), Is.EqualTo(0));
                Assert.That(GetPrivateField(panel, "_observedComponentNodes")!.GetType()
                    .GetProperty("Count")!.GetValue(GetPrivateField(panel, "_observedComponentNodes")), Is.EqualTo(0));
                Assert.That(HasHandler(typeof(Selection), "SelectionChanged", panel), Is.False);
                Assert.That(child.IsDestroyed, Is.False);
                Assert.That(child.Parent, Is.SameAs(node));
                Assert.That(list.VirtualProvider, Is.Null);
                Assert.That(retainedRow.Parent, Is.SameAs(list));
            });
        }
        finally
        {
            Selection.SelectionChanged -= handler;
            observed.Destroy(true);
            node.Destroy(true);
        }
    }

    private static SceneNode NewPanelNode()
    {
        SceneNode node = new("Panel") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        return node;
    }

    private static MethodInfo PrivateMethod(object owner, string name)
        => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static object? GetPrivateField(object owner, string name)
    {
        for (Type? type = owner.GetType(); type is not null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is { } field)
                return field.GetValue(owner);
        throw new MissingFieldException(owner.GetType().Name, name);
    }

    private static void SetPrivateField(object owner, string name, object? value)
    {
        for (Type? type = owner.GetType(); type is not null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is { } field)
            {
                field.SetValue(owner, value);
                return;
            }
        throw new MissingFieldException(owner.GetType().Name, name);
    }

    private static bool HasHandler(Type type, string eventName, object owner, object? source = null)
        => (type.GetField(eventName, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(source) as Delegate)?.GetInvocationList().Any(item => ReferenceEquals(item.Target, owner)) == true;
}
