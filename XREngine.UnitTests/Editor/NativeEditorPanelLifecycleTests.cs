using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Editor;
using XREngine.Editor.UI.Components;
using XREngine.Editor.UI.Toolbar;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public class NativeEditorPanelLifecycleTests
{
    [Test]
    public void ToolbarRebuild_FromChildDestruction_DefersTheSecondRebuild()
    {
        SceneNode node = new("Toolbar");
        UIToolbarComponent toolbar = node.AddComponent<UIToolbarComponent>()!;
        SceneNode child = new(node, "OldToolbarChild");
        int calls = 0;
        child.Destroying += _ =>
        {
            calls++;
            toolbar.RemakeToolbarItems();
            return true;
        };

        Assert.DoesNotThrow(toolbar.RemakeToolbarItems);
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(child.IsDestroyed, Is.True);
        typeof(UIToolbarComponent).GetMethod("CompleteDeferredRemake",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(toolbar, null);
        Assert.That(node.Transform.ChildCount, Is.EqualTo(1));
        node.Destroy(true);
    }

    [Test]
    public void ToolbarBinding_DeactivatedByPropertyCallback_DoesNotResume()
    {
        SceneNode node = new("Toolbar");
        UIToolbarComponent toolbar = node.AddComponent<UIToolbarComponent>()!;
        SceneNode buttonNode = new(node, "Button");
        UIButtonComponent button = buttonNode.AddComponent<UIButtonComponent>()!;
        ToolbarButton item = new("Action", (Action<UIInteractableComponent>?)null);
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ToolbarItemBase.InteractableComponent) &&
                item.InteractableComponent is not null)
                InvokeComponentDeactivated(toolbar);
        };

        object? result = typeof(UIToolbarComponent).GetMethod("BindItem",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(toolbar, [item, button]);
        Assert.That(result, Is.False);
        Assert.That(item.InteractableComponent, Is.Null);
        Assert.That(item.ParentToolbarComponent, Is.Null);
        node.Destroy(true);
    }

    [Test]
    public void ToolbarReactivatedDuringBindingRelease_SchedulesItsRebuild()
    {
        SceneNode node = new("Toolbar");
        UIToolbarComponent toolbar = node.AddComponent<UIToolbarComponent>()!;
        ToolbarButton item = new("Action", (Action<UIInteractableComponent>?)null);
        toolbar.RootMenuOptions = [item];
        bool reactivated = false;
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ToolbarItemBase.InteractableComponent) ||
                item.InteractableComponent is not null || reactivated)
                return;
            reactivated = true;
            typeof(UIToolbarComponent).GetMethod("OnComponentActivated",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(toolbar, null);
        };

        InvokeComponentDeactivated(toolbar);
        Assert.That(reactivated, Is.True);
        Assert.That(typeof(UIToolbarComponent).GetField("_remakeTickRegistered",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(toolbar), Is.True);
        typeof(UIToolbarComponent).GetMethod("CompleteDeferredRemake",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(toolbar, null);
        Assert.That(item.InteractableComponent, Is.Not.Null);
        Assert.That(item.ParentToolbarComponent, Is.SameAs(toolbar));
        node.Destroy(true);
    }

    [TestCase(typeof(HierarchyPanel))]
    [TestCase(typeof(InspectorPanel))]
    [TestCase(typeof(UIToolbarComponent))]
    [TestCase(typeof(UIEditorComponent))]
    public void EditorPanelDeactivation_DoesNotDetachChildSceneNodes(Type componentType)
    {
        SceneNode node = new("EditorPanelNode");
        SceneNode child = new(node, "ExistingChild");
        XRComponent? component = node.AddComponent(componentType);
        Assert.That(component, Is.Not.Null);

        InvokeComponentDeactivated(component!);

        Assert.Multiple(() =>
        {
            Assert.That(child.IsDestroyed, Is.False);
            Assert.That(child.Parent, Is.SameAs(node));
            Assert.That(node.Transform.Children.Any(t => ReferenceEquals(t, child.Transform)), Is.True);
        });
    }

    private static void InvokeComponentDeactivated(XRComponent component)
    {
        MethodInfo? method = component.GetType().GetMethod(
            "OnComponentDeactivated",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.That(method, Is.Not.Null);
        method!.Invoke(component, null);
    }
}
