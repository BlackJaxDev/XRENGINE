using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Editor;
using XREngine.Editor.UI.Components;
using XREngine.Editor.UI.Toolbar;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

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

    [Test]
    public void EditorComponentDirectDestroyRetiresOnlyItsOwnedRootAndRejectsReentry()
    {
        SceneNode node = new("Editor owner") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        UIEditorComponent editor = node.AddComponent<UIEditorComponent>()!;
        SceneNode foreign = new(node, "Foreign child");
        SceneNode owned = new(node, "EditorRoot");
        typeof(UIEditorComponent).GetField("_editorRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(editor, owned);
        int reentries = 0;
        owned.Destroying += _ =>
        {
            reentries++;
            editor.RemakeChildren();
            return true;
        };
        try
        {
            editor.Destroy(true);
            Assert.Multiple(() =>
            {
                Assert.That(reentries, Is.EqualTo(1));
                Assert.That(owned.IsDestroyed, Is.True);
                Assert.That(foreign.IsDestroyed, Is.False);
                Assert.That(foreign.Parent, Is.SameAs(node));
                Assert.That(node.Transform.Children.Count, Is.EqualTo(1));
            });
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void EditorOwnedRootReparentedDuringDeactivationIsPreserved()
    {
        SceneNode node = new("Editor owner") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        SceneNode foreignParent = new("Foreign owner") { IsActiveSelf = false };
        UIEditorComponent editor = node.AddComponent<UIEditorComponent>()!;
        SceneNode owned = new(node, "EditorRoot");
        typeof(UIEditorComponent).GetField("_editorRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(editor, owned);
        owned.Deactivated += _ => owned.Transform.SetParent(
            foreignParent.Transform, false, EParentAssignmentMode.Immediate);
        try
        {
            editor.Destroy(true);
            Assert.Multiple(() =>
            {
                Assert.That(owned.IsDestroyed, Is.False);
                Assert.That(owned.Parent, Is.SameAs(foreignParent));
                Assert.That(owned.IsActiveSelf, Is.False);
            });
        }
        finally { node.Destroy(true); foreignParent.Destroy(true); }
    }

    [Test]
    public void DisablingEditorComponentRetiresOnlyItsOwnedRoot()
    {
        SceneNode node = new("Editor owner") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        UIEditorComponent editor = node.AddComponent<UIEditorComponent>()!;
        SceneNode owned = new(node, "EditorRoot");
        SceneNode foreign = new(node, "Foreign child");
        typeof(UIEditorComponent).GetField("_editorRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(editor, owned);
        // This exercises the public component-disable route without building the full editor UI.
        typeof(XRComponent).GetField("_componentActivated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(editor, true);
        try
        {
            editor.IsActive = false;
            Assert.Multiple(() =>
            {
                Assert.That(editor.IsDestroyed, Is.False);
                Assert.That(owned.IsDestroyed, Is.True);
                Assert.That(foreign.IsDestroyed, Is.False);
                Assert.That(foreign.Parent, Is.SameAs(node));
                Assert.That(node.IsDestroyed, Is.False);
            });
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void EditorBuildChildEventDestroyRetiresUnpublishedNewRoot()
    {
        SceneNode node = new("Editor owner") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        UIEditorComponent editor = node.AddComponent<UIEditorComponent>()!;
        SceneNode? created = null;
        node.Transform.Children.PostAnythingAdded += child =>
        {
            if (created is not null) return;
            created = child.SceneNode;
            editor.Destroy(true);
        };
        try
        {
            editor.RemakeChildren();
            Assert.Multiple(() =>
            {
                Assert.That(editor.IsDestroyed, Is.True);
                Assert.That(created, Is.Not.Null);
                Assert.That(created!.IsDestroyed, Is.True);
                Assert.That(node.Transform.ChildCount, Is.Zero);
            });
        }
        finally { node.Destroy(true); }
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
