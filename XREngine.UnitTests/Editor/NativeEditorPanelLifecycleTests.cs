using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Editor;
using XREngine.Editor.UI.Components;
using XREngine.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public class NativeEditorPanelLifecycleTests
{
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
