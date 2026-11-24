using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UberRenderPresetTests
{
    private static XRMaterial CreateMaterial()
        => new()
        {
            Parameters =
            [
                new ShaderInt(0, "_Mode"),
                new ShaderFloat(0.5f, "_Cutoff"),
                new ShaderFloat(0.0f, "_AlphaPremultiply"),
                new ShaderFloat(1.0f, "_AlphaForceOpaque"),
            ],
        };

    [Test]
    public void EveryPreset_AppliesItsStateAndIsDetected()
    {
        foreach (UberRenderPresetDefinition definition in UberRenderPresets.All)
        {
            XRMaterial material = CreateMaterial();

            UberRenderPresets.Apply(material, definition.Preset);

            material.TransparencyMode.ShouldBe(definition.TransparencyMode);
            material.AlphaCutoff.ShouldBe(definition.AlphaCutoff);
            material.Parameter<ShaderFloat>("_Cutoff")!.Value.ShouldBe(definition.AlphaCutoff);
            material.Parameter<ShaderInt>("_Mode")!.Value.ShouldBe(definition.ShaderMode);
            material.Parameter<ShaderFloat>("_AlphaPremultiply")!.Value.ShouldBe(definition.Premultiply ? 1.0f : 0.0f);
            material.RenderOptions!.DepthTest.UpdateDepth.ShouldBe(definition.WritesDepth);
            BlendMode blend = material.RenderOptions.BlendModeAllDrawBuffers!;
            blend.Enabled.ShouldBe(definition.Blend ? ERenderParamUsage.Enabled : ERenderParamUsage.Disabled);
            if (definition.Blend)
            {
                blend.RgbSrcFactor.ShouldBe(definition.SourceFactor);
                blend.RgbDstFactor.ShouldBe(definition.DestinationFactor);
            }

            UberRenderPresets.TryDetect(material, out EUberRenderPreset detected).ShouldBeTrue();
            detected.ShouldBe(definition.Preset);
        }
    }

    [Test]
    public void TransparentCutout_UsesMaskedPassWithBlending()
    {
        XRMaterial material = CreateMaterial();

        UberRenderPresets.Apply(material, EUberRenderPreset.TransparentCutout);

        material.RenderPass.ShouldBe((int)EDefaultRenderPass.MaskedForward);
        material.RenderOptions!.BlendModeAllDrawBuffers!.Enabled.ShouldBe(ERenderParamUsage.Enabled);
        material.RenderOptions.DepthTest.UpdateDepth.ShouldBeTrue();
    }

    [Test]
    public void ChangedBlendState_IsNotDetectedAsPreset()
    {
        XRMaterial material = CreateMaterial();
        UberRenderPresets.Apply(material, EUberRenderPreset.Multiply);

        material.RenderOptions!.BlendModeAllDrawBuffers!.RgbDstFactor = EBlendingFactor.One;

        UberRenderPresets.TryDetect(material, out _).ShouldBeFalse();
    }

    [TestCase(EUberRenderPreset.TransparentCutout)]
    [TestCase(EUberRenderPreset.Multiply)]
    [TestCase(EUberRenderPreset.Additive)]
    public void AppliedPreset_SurvivesSerialization(EUberRenderPreset preset)
    {
        XRMaterial material = CreateMaterial();
        UberRenderPresets.Apply(material, preset);

        string yaml = AssetManager.Serializer.Serialize(material, typeof(XRMaterial));
        XRMaterial? clone = AssetManager.Deserializer.Deserialize<XRMaterial>(yaml);

        clone.ShouldNotBeNull();
        UberRenderPresets.TryDetect(clone!, out EUberRenderPreset detected).ShouldBeTrue();
        detected.ShouldBe(preset);
    }

    [TestCase(SourceToonRenderPreset.Opaque, EUberRenderPreset.Opaque)]
    [TestCase(SourceToonRenderPreset.Cutout, EUberRenderPreset.Cutout)]
    [TestCase(SourceToonRenderPreset.TransClipping, EUberRenderPreset.TransparentCutout)]
    [TestCase(SourceToonRenderPreset.Fade, EUberRenderPreset.Fade)]
    [TestCase(SourceToonRenderPreset.Transparent, EUberRenderPreset.Transparent)]
    [TestCase(SourceToonRenderPreset.Additive, EUberRenderPreset.Additive)]
    [TestCase(SourceToonRenderPreset.SoftAdditive, EUberRenderPreset.SoftAdditive)]
    [TestCase(SourceToonRenderPreset.Multiplicative, EUberRenderPreset.Multiply)]
    [TestCase(SourceToonRenderPreset.Multiplicative2X, EUberRenderPreset.Multiply2X)]
    public void SourcePresets_MapToUberPresets(SourceToonRenderPreset source, EUberRenderPreset expected)
        => SourceToonRenderStateConverter.ToUberPreset(source).ShouldBe(expected);
}
