using System.Reflection;
using NUnit.Framework;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public class UIRenderableMaterialTeardownTests
{
    [Test]
    public void MaterialReplacementAndDirectDestroyDetachTheExactSubscribedMaterial()
    {
        using UITestShaderScope shaders = new();
        using IDisposable materialTarget = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
            EngineMaterialConstructionTarget.DesktopGlsl);
        SceneNode node = new("Material owner") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        UIMaterialComponent component = node.AddComponent<UIMaterialComponent>()!;
        XRMaterial first = new();
        XRMaterial second = new();
        try
        {
            component.Material = first;
            Assert.That(HasHandler(first, component), Is.False,
                "An inactive component must not become a material callback owner.");
            node.SetWorldContext(new TestWorldContext());
            node.IsActiveSelf = true;
            Assert.That(HasHandler(first, component), Is.True);
            component.Material = second;
            Assert.Multiple(() =>
            {
                Assert.That(HasHandler(first, component), Is.False);
                Assert.That(HasHandler(second, component), Is.True);
            });

            node.IsActiveSelf = false;
            Assert.That(HasHandler(second, component), Is.False);
            component.Material = first;
            Assert.That(HasHandler(first, component), Is.False);
            node.IsActiveSelf = true;
            Assert.That(HasHandler(first, component), Is.True);

            // A silent field replacement must not change which external event owns the handler.
            typeof(UIRenderableComponent).GetField("_material", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(component, second);
            component.Destroy(true);
            Assert.Multiple(() =>
            {
                Assert.That(HasHandler(first, component), Is.False);
                Assert.That(HasHandler(second, component), Is.False);
            });
            component.Material = first;
            Assert.That(HasHandler(first, component), Is.False);
        }
        finally { node.Destroy(true); first.Destroy(true); second.Destroy(true); }
    }

    [Test]
    public void PropertyCallbackDestroyCannotAttachReplacementMaterial()
    {
        using UITestShaderScope shaders = new();
        using IDisposable materialTarget = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
            EngineMaterialConstructionTarget.DesktopGlsl);
        SceneNode node = new("Material reentry") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        UIMaterialComponent component = node.AddComponent<UIMaterialComponent>()!;
        XRMaterial source = new();
        try
        {
            node.SetWorldContext(new TestWorldContext());
            node.IsActiveSelf = true;
            component.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(UIRenderableComponent.Material))
                    component.Destroy(true);
            };
            component.Material = source;
            Assert.That(HasHandler(source, component), Is.False);
            Assert.That(component.IsDestroyed, Is.True);
        }
        finally { node.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void ReentrantMaterialReplacementKeepsOnlyTheFinalActiveOwner()
    {
        using UITestShaderScope shaders = new();
        using IDisposable materialTarget = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
            EngineMaterialConstructionTarget.DesktopGlsl);
        SceneNode node = new("Material handoff") { IsActiveSelf = false };
        node.SetTransform<UIBoundableTransform>();
        UIMaterialComponent component = node.AddComponent<UIMaterialComponent>()!;
        XRMaterial first = new();
        XRMaterial second = new();
        try
        {
            node.SetWorldContext(new TestWorldContext());
            node.IsActiveSelf = true;
            component.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(UIRenderableComponent.Material) &&
                    ReferenceEquals(component.Material, first))
                    component.Material = second;
            };
            component.Material = first;
            Assert.Multiple(() =>
            {
                Assert.That(component.Material, Is.SameAs(second));
                Assert.That(HasHandler(first, component), Is.False);
                Assert.That(HasHandler(second, component), Is.True);
            });
            node.IsActiveSelf = false;
            Assert.That(HasHandler(second, component), Is.False);
        }
        finally { node.Destroy(true); first.Destroy(true); second.Destroy(true); }
    }

    private static bool HasHandler(XRMaterial material, UIRenderableComponent owner)
        => (typeof(XRMaterialBase).GetField("SettingUniforms", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(material) as Delegate)?.GetInvocationList()
            .Any(handler => ReferenceEquals(handler.Target, owner)) == true;
}
