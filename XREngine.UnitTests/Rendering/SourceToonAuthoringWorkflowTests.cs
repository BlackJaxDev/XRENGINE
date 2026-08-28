using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Editor.MaterialAuthoring;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class SourceToonAuthoringWorkflowTests
{
    [Test]
    public void RenderStateAdapter_AppliesNativeStateAndRestoresExactSnapshot()
    {
        XRMaterial material = new();
        BlendMode? original = material.RenderOptions.BlendModeAllDrawBuffers;
        Action undo = MaterialRenderStateActionAdapter.CaptureUndo(material, "_SrcBlend");

        MaterialRenderStateActionAdapter.Apply(material, "_SrcBlend", "5");

        material.RenderOptions.BlendModeAllDrawBuffers!.RgbSrcFactor
            .ShouldBe(EBlendingFactor.SrcAlpha);
        MaterialAuthoringMetadataStore.Instance.Get(material)
            .LocalOverrides["renderState:_SrcBlend"].ShouldBe("5");

        undo();

        if (original is null)
            material.RenderOptions.BlendModeAllDrawBuffers.ShouldBeNull();
        else
            material.RenderOptions.BlendModeAllDrawBuffers!.RgbSrcFactor
                .ShouldBe(original.RgbSrcFactor);
        MaterialAuthoringMetadataStore.Instance.Get(material)
            .LocalOverrides.ContainsKey("renderState:_SrcBlend").ShouldBeFalse();
    }

    [Test]
    public void LayeredValues_RevertWithoutDestroyingLowerPrioritySources()
    {
        MaterialAuthoringLayeredValue<string> value = new();
        value.Apply(EMaterialAuthoringValueLayer.Imported, "imported");
        value.Apply(EMaterialAuthoringValueLayer.Preset, "preset");
        value.Apply(EMaterialAuthoringValueLayer.Local, "local");

        value.TryResolve(out string? resolved, out EMaterialAuthoringValueLayer layer)
            .ShouldBeTrue();
        resolved.ShouldBe("local");
        layer.ShouldBe(EMaterialAuthoringValueLayer.Local);

        value.Revert(EMaterialAuthoringValueLayer.Local).ShouldBeTrue();
        value.TryResolve(out resolved, out layer).ShouldBeTrue();
        resolved.ShouldBe("preset");
        layer.ShouldBe(EMaterialAuthoringValueLayer.Preset);
    }

    [Test]
    public void LinkRegistry_RoundTripsInDeterministicVersionedEnvelope()
    {
        MaterialLinkRegistry source = new();
        MaterialAuthoringPersistentLinkGroup group = new(
            MaterialAuthoringPersistentLinkGroup.CurrentVersion,
            Guid.NewGuid(),
            "Eyes",
            "poiyomi/property/emission",
            [
                new("Assets/a.xrm", "poiyomi-toon-9.3.64"),
                new("Assets/b.xrm", "poiyomi-toon-9.3.64"),
            ]);
        source.AddOrReplace(group).ShouldBeNull();

        MaterialLinkRegistryPersistence.TryDeserialize(
            MaterialLinkRegistryPersistence.Serialize(source),
            out MaterialLinkRegistry restored,
            out IReadOnlyList<string> diagnostics).ShouldBeTrue();

        diagnostics.ShouldBeEmpty();
        MaterialAuthoringPersistentLinkGroup restoredGroup = restored.Groups.Single();
        restoredGroup.Id.ShouldBe(group.Id);
        restoredGroup.Name.ShouldBe(group.Name);
        restoredGroup.SemanticPropertyId.ShouldBe(group.SemanticPropertyId);
        restoredGroup.Members.ShouldBe(group.Members);
    }
}
