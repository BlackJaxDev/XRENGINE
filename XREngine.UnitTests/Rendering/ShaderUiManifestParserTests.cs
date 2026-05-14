using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Materials;

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
            //@property(name="_MatcapIntensity", display="Intensity", mode=constant, range=[0,2])
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
    public void Parse_PropertyToggleAndEnumMetadata_AreCaptured()
    {
        const string source = """
            #version 450 core
            //@property(name="_HideInShadow", display="Hide In Shadow", mode=constant, toggle=true)
            uniform float _HideInShadow;
            //@property(name="_BlendMode", display="Blend Mode", mode=constant, enum="0:Mix|1:Add|2:Multiply")
            uniform float _BlendMode;
            """;

        ShaderUiManifest manifest = ShaderUiManifestParser.Parse(source);

        manifest.ValidationIssues.ShouldBeEmpty();
        manifest.PropertyLookup["_HideInShadow"].IsToggle.ShouldBeTrue();
        manifest.PropertyLookup["_BlendMode"].EnumOptions.ShouldBe("0:Mix|1:Add|2:Multiply");
    }

    [Test]
    public void Parse_BindingAnnotation_CapturesMaterialScope()
    {
        const string source = """
            #version 450 core
            //@binding(name="BaseColor", scope=material, semantic=baseColorOpacity, storage=field, default="vec4(1.0)")
            uniform vec4 BaseColor;
            //@binding(name="Texture0", scope=texture, semantic=albedo, storage=bindless)
            layout(binding = 0) uniform sampler2D Texture0;
            """;

        ShaderUiManifest manifest = ShaderUiManifestParser.Parse(source);

        manifest.ValidationIssues.ShouldBeEmpty();
        manifest.Bindings.Count.ShouldBe(2);
        manifest.BindingLookup["BaseColor"].Scope.ShouldBe(EMaterialBindingScope.Material);
        manifest.BindingLookup["BaseColor"].Storage.ShouldBe(EMaterialBindingStorage.Field);
        manifest.BindingLookup["BaseColor"].Semantic.ShouldBe("baseColorOpacity");
        manifest.BindingLookup["Texture0"].Scope.ShouldBe(EMaterialBindingScope.Texture);
        manifest.BindingLookup["Texture0"].Storage.ShouldBe(EMaterialBindingStorage.Bindless);
    }

    [Test]
    public void Parse_PropertyIndirectKeys_CaptureBindingMetadata()
    {
        const string source = """
            #version 450 core
            //@property(name="_MainTex", display="Albedo Map", slot=texture, indirect=texture, semantic=albedo)
            uniform sampler2D _MainTex;
            //@property(name="_Color", display="Tint", mode=static, indirect=field, semantic=baseColorOpacity, default="vec4(1.0)")
            uniform vec4 _Color;
            """;

        ShaderUiManifest manifest = ShaderUiManifestParser.Parse(source);

        manifest.ValidationIssues.ShouldBeEmpty();
        manifest.PropertyLookup["_MainTex"].Binding.ShouldNotBeNull();
        manifest.PropertyLookup["_MainTex"].Binding!.Scope.ShouldBe(EMaterialBindingScope.Texture);
        manifest.PropertyLookup["_MainTex"].Binding!.Semantic.ShouldBe("albedo");
        manifest.PropertyLookup["_Color"].Binding.ShouldNotBeNull();
        manifest.PropertyLookup["_Color"].Binding!.Scope.ShouldBe(EMaterialBindingScope.Material);
        manifest.PropertyLookup["_Color"].Binding!.Storage.ShouldBe(EMaterialBindingStorage.Field);
    }

    [Test]
    public void Parse_BindingMissingScope_ReportsCompatibilityWarning()
    {
        const string source = """
            #version 450 core
            //@binding(name="Tint", semantic=baseColorOpacity, storage=field)
            uniform vec4 Tint;
            """;

        ShaderUiManifest manifest = ShaderUiManifestParser.Parse(source);

        manifest.BindingLookup["Tint"].Scope.ShouldBe(EMaterialBindingScope.Unspecified);
        manifest.ValidationIssues.ShouldContain(x =>
            x.Severity == EShaderUiValidationSeverity.Warning &&
            x.Message.Contains("missing a scope", StringComparison.Ordinal));
    }

    [Test]
    public void Parse_BindingSamplerAsMaterialField_ReportsTypeError()
    {
        const string source = """
            #version 450 core
            //@binding(name="Texture0", scope=material, semantic=baseColorOpacity, storage=field)
            uniform sampler2D Texture0;
            """;

        ShaderUiManifest manifest = ShaderUiManifestParser.Parse(source);

        manifest.ValidationIssues.ShouldContain(x =>
            x.Severity == EShaderUiValidationSeverity.Error &&
            x.Message.Contains("sampler", StringComparison.Ordinal));
    }

    [Test]
    public void Parse_PropertyTooltipNamedArgument_IsCaptured()
    {
        const string source = """
            #version 450 core
            //@property(name="_MainTex", display="Main Texture", tooltip="Primary albedo sampler")
            uniform sampler2D _MainTex;
            """;

        ShaderUiManifest manifest = ShaderUiManifestParser.Parse(source);

        manifest.ValidationIssues.ShouldBeEmpty();
        manifest.PropertyLookup["_MainTex"].Tooltip.ShouldBe("Primary albedo sampler");
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

    [Test]
    public void Parse_UberSourcePath_MissingExplicitPropertyMetadata_ReportsWarning()
    {
        const string source = """
            #version 450 core
            //@feature(id="glitter", name="Glitter", default=off)
            #ifndef XRENGINE_UBER_DISABLE_GLITTER
            uniform float _GlitterBrightness;
            #endif
            """;

        ShaderUiManifest manifest = ShaderUiManifestParser.Parse(source, @"Build\CommonAssets\Shaders\Uber\uniforms.glsl");

        manifest.ValidationIssues.ShouldContain(x =>
            x.Severity == EShaderUiValidationSeverity.Warning &&
            x.Message.Contains("_GlitterBrightness", StringComparison.Ordinal));
    }

    [Test]
    public void Parse_UberHelperUniforms_DoNotRequireExplicitPropertyMetadata()
    {
        const string source = """
            #version 450 core
            //@feature(id="glitter", name="Glitter", default=off)
            #ifndef XRENGINE_UBER_DISABLE_GLITTER
            uniform float _EnableGlitter;
            uniform vec4 _GlitterMask_ST;
            #endif
            """;

        ShaderUiManifest manifest = ShaderUiManifestParser.Parse(source, @"Build\CommonAssets\Shaders\Uber\uniforms.glsl");

        manifest.ValidationIssues.ShouldNotContain(x => x.Message.Contains("_EnableGlitter", StringComparison.Ordinal));
        manifest.ValidationIssues.ShouldNotContain(x => x.Message.Contains("_GlitterMask_ST", StringComparison.Ordinal));
    }

    [Test]
    public void Parse_RealUberUniformSurface_HasNoMissingMetadataWarnings()
    {
        string path = Path.Combine(ResolveRepoRoot(), "Build", "CommonAssets", "Shaders", "Uber", "uniforms.glsl");
        string source = File.ReadAllText(path);

        ShaderUiManifest manifest = ShaderUiManifestParser.Parse(source, path);

        manifest.ValidationIssues.ShouldBeEmpty();
        manifest.PropertyLookup["_MainVertexColoringEnabled"].HasExplicitMetadata.ShouldBeTrue();
        manifest.PropertyLookup["_LightingMapMode"].HasExplicitMetadata.ShouldBeTrue();
        manifest.PropertyLookup["_RimHideInShadow"].IsToggle.ShouldBeTrue();
        manifest.PropertyLookup["_BackFaceBlendMode"].EnumOptions.ShouldBe("0:Mix|1:Add|2:Multiply");
    }

    private static string ResolveRepoRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
