using NUnit.Framework;
using XREngine.Components;
using XREngine.Input.Devices;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class XRComponentSiblingTests
{
    [Test]
    public void SiblingLookup_OnDetachedComponent_ReturnsEmptyResults()
    {
        var component = new DetachedComponent();

        Assert.That(component.TryGetSiblingComponent<DetachedComponent>(out var sibling), Is.False);
        Assert.That(sibling, Is.Null);
        Assert.That(component.GetSiblingComponent<DetachedComponent>(), Is.Null);
        Assert.That(component.GetSiblingComponent<DetachedComponent>(createIfNotExist: true), Is.Null);
        Assert.That(component.GetSiblingComponents<DetachedComponent>(), Is.Empty);
    }

    [Test]
    public void RegisterOptionalInputs_OnDetachedPawn_DoesNotRequireSceneNode()
    {
        var pawn = new PawnComponent();
        var input = new LocalInputInterface();

        Assert.DoesNotThrow(() => pawn.RegisterOptionalInputs(input));

        input.Unregister = true;
        Assert.DoesNotThrow(() => pawn.RegisterOptionalInputs(input));
    }

    private sealed class DetachedComponent : XRComponent;
}
