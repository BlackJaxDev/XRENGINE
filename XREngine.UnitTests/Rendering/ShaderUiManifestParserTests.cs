using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ShaderUiManifestParserTests
{
    [Test]
    public void Parse_AnnotatedFeatureAndProperties_BindsMetadataToGuardedSection()
    {
        const string source = """
            #version 450 core
            //@category("Effects", order=3)
            //@feature(id="matcap", name="Matcap", default=off, cost=medium)
            //@tooltip("Sphere-mapped highlight overlay.")
            #ifndef XRENGINE_UBER_DISABLE_MATCAP
            //@property(name="_MatcapTex", display="Matcap Texture", slot=texture)
            uniform sampler2D _MatcapTex;
            //@property(name="_MatcapIntensity", display="Intensity", mode=static, range=[0,2])
            uniform float _MatcapIntensity;
            #endif
            """;

        ShaderUiManifest manifest = ShaderUiManifestParser.Parse(source);

        manifest.ValidationIssues.ShouldBeEmpty();
        manifest.Features.Count.ShouldBe(1);

        ShaderUiFeature feature = manifest.Features[0];
        feature.Id.ShouldBe("matcap");
        feature.DisplayName.ShouldBe("Matcap");
        feature.Category.ShouldBe("Effects");
        feature.DefaultEnabled.ShouldBeTrue();
        feature.HasExplicitMetadata.ShouldBeTrue();

        ShaderUiProperty sampler = manifest.PropertyLookup["_MatcapTex"];
        sampler.IsSampler.ShouldBeTrue();
        sampler.DisplayName.ShouldBe("Matcap Texture");
        sampler.FeatureId.ShouldBe("matcap");
        sampler.Category.ShouldBe("Effects");

        ShaderUiProperty scalar = manifest.PropertyLookup["_MatcapIntensity"];
        scalar.DefaultMode.ShouldBe(EShaderUiPropertyMode.Static);
        scalar.Range.ShouldBe("[0,2]");
        scalar.FeatureId.ShouldBe("matcap");
    }

    [Test]
    public void Parse_ImplicitUberDisableGuard_CreatesInferredFeatureWithDisabledDefault()
    {
        const string source = """
            #version 450 core
            #define XRENGINE_UBER_DISABLE_PARALLAX 1
            #ifndef XRENGINE_UBER_DISABLE_PARALLAX
            uniform float _EnableParallax;
            #endif
            """;

        ShaderUiManifest manifest = ShaderUiManifestParser.Parse(source);

        manifest.ValidationIssues.ShouldBeEmpty();
        manifest.Features.Count.ShouldBe(1);

        ShaderUiFeature feature = manifest.Features[0];
        feature.Id.ShouldBe("parallax");
        feature.DefaultEnabled.ShouldBeFalse();
        feature.HasExplicitMetadata.ShouldBeFalse();
        feature.GuardMacro.ShouldBe("XRENGINE_UBER_DISABLE_PARALLAX");

        manifest.PropertyLookup["_EnableParallax"].FeatureId.ShouldBe("parallax");
    }

    [Test]
    public void Parse_InlineUniformComment_BecomesFallbackTooltip()
    {
        const string source = """
            #version 450 core
            uniform float _Cutoff; // Alpha cutoff threshold
            """;

        ShaderUiManifest manifest = ShaderUiManifestParser.Parse(source);

        manifest.ValidationIssues.ShouldBeEmpty();
        manifest.Properties.Count.ShouldBe(1);
        manifest.Properties[0].Tooltip.ShouldBe("Alpha cutoff threshold");
        manifest.Properties[0].DefaultMode.ShouldBe(EShaderUiPropertyMode.Static);
    }
}