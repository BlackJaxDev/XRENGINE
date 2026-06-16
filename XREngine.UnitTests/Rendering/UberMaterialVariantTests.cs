using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UberMaterialVariantTests
{
    [SetUp]
    public void SetUp()
        => UberShaderVariantBuilder.ClearCachesForTests();

    [TearDown]
    public void TearDown()
        => UberShaderVariantBuilder.ClearCachesForTests();

    [Test]
    public void EnsureUberStateInitialized_UsesManifestFeatureDefaultAndPropertyModes()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            #define XRENGINE_UBER_DISABLE_EMISSION 1
            //@feature(id="emission", name="Emission", default=on)
            #ifndef XRENGINE_UBER_DISABLE_EMISSION
            //@property(name="_EmissionColor", display="Emission Color", mode=static)
            uniform vec4 _EmissionColor;
            #endif
            //@property(name="_Color", display="Color", mode=animated)
            uniform vec4 _Color;
            """);

        material.EnsureUberStateInitialized();

        material.IsUberFeatureEnabled("emission", defaultEnabled: true).ShouldBeTrue();
        material.GetUberPropertyMode("_EmissionColor", EShaderUiPropertyMode.Unspecified, isSampler: false).ShouldBe(EShaderUiPropertyMode.Static);
        material.GetUberPropertyMode("_Color", EShaderUiPropertyMode.Unspecified, isSampler: false).ShouldBe(EShaderUiPropertyMode.Animated);
    }

    [Test]
    public void EnsureUberStateInitialized_UsesMutabilityAsPropertyModeContract()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            //@property(name="_RuntimeValue", display="Runtime Value", mode=static, mutability=runtime)
            uniform float _RuntimeValue;
            //@property(name="_ShadowType", display="Shadow Type", mode=animated, mutability=material-static)
            uniform int _ShadowType;
            """);

        material.EnsureUberStateInitialized();

        material.GetUberPropertyMode("_RuntimeValue", EShaderUiPropertyMode.Static, isSampler: false).ShouldBe(EShaderUiPropertyMode.Animated);
        material.GetUberPropertyMode("_ShadowType", EShaderUiPropertyMode.Animated, isSampler: false).ShouldBe(EShaderUiPropertyMode.Static);
    }

    [Test]
    public void RequestUberVariantRebuild_StripsCanonicalFeatureGuard_WhenFeatureIsEnabled()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            #define XRENGINE_UBER_DISABLE_EMISSION 1
            //@feature(id="emission", name="Emission", default=on)
            #ifndef XRENGINE_UBER_DISABLE_EMISSION
            //@property(name="_EmissionColor", display="Emission Color", mode=static)
            uniform vec4 _EmissionColor;
            #endif
            """,
            new ShaderVector4(new Vector4(0.2f, 0.1f, 0.4f, 1.0f), "_EmissionColor"));

        material.EnsureUberStateInitialized();

        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);

        string generatedSource = GetFragmentSource(material);
        generatedSource.ShouldContain("XRENGINE_UBER_GENERATED_VARIANT");
        generatedSource.ShouldNotContain("#define XRENGINE_UBER_DISABLE_EMISSION 1");
        generatedSource.ShouldNotContain("#define _EmissionColor ");
        generatedSource.ShouldNotContain("uniform vec4 _EmissionColor;");
        material.ActiveUberVariant.EnabledFeatures.ShouldContain("emission");
    }

    [Test]
    public void RequestUberVariantRebuild_StaticPropertyAndDisabledFeatureArePrunedWhenUnreferenced()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            //@property(name="_Color", display="Color", mode=static)
            uniform vec4 _Color;
            //@feature(id="emission", name="Emission", default=on)
            #ifndef XRENGINE_UBER_DISABLE_EMISSION
            //@property(name="_EmissionColor", display="Emission Color", mode=static)
            uniform vec4 _EmissionColor;
            #endif
            """,
            new ShaderVector4(new Vector4(1.0f, 0.5f, 0.25f, 1.0f), "_Color"),
            new ShaderVector4(new Vector4(0.2f, 0.1f, 0.4f, 1.0f), "_EmissionColor"));

        material.EnsureUberStateInitialized();
        material.SetUberFeatureEnabled("emission", false).ShouldBeTrue();

        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);

        XRShader fragmentShader = material.GetShader(EShaderType.Fragment)!;
        string generatedSource = fragmentShader.Source?.Text ?? string.Empty;

        generatedSource.ShouldContain("XRENGINE_UBER_GENERATED_VARIANT");
        generatedSource.ShouldNotContain("#define XRENGINE_UBER_DISABLE_EMISSION 1");
        generatedSource.ShouldNotContain("#define _Color ");
        generatedSource.ShouldNotContain("uniform vec4 _Color;");
        generatedSource.ShouldNotContain("uniform vec4 _EmissionColor;");

        material.ActiveUberVariant.StaticProperties.ShouldContain("_Color=vec4(1.0, 0.5, 0.25, 1.0)");
        material.ActiveUberVariant.StaticProperties.ShouldNotContain("_EmissionColor=vec4(0.2, 0.1, 0.4, 1.0)");
        material.ActiveUberVariant.EnabledFeatures.ShouldNotContain("emission");
    }

    [Test]
    public void RequestUberVariantRebuild_EmitsGeneratedMetadataAndParseableVariantHash()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            //@property(name="_Color", display="Color", mode=static)
            uniform vec4 _Color;
            vec4 ResolveColor() { return _Color; }
            """,
            new ShaderVector4(new Vector4(0.3f, 0.4f, 0.5f, 1.0f), "_Color"));

        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);

        XRShader fragmentShader = material.GetShader(EShaderType.Fragment).ShouldNotBeNull();
        string generatedSource = fragmentShader.Source?.Text ?? string.Empty;
        ulong variantHash = material.ActiveUberVariant.VariantHash;

        fragmentShader.IsGeneratedUberVariant.ShouldBeTrue();
        fragmentShader.GeneratedUberVariantHash.ShouldBe(variantHash);
        UberShaderVariantBuilder.IsGeneratedVariant(fragmentShader).ShouldBeTrue();
        generatedSource.ShouldContain($"// variant-hash: 0x{variantHash:x16}");
        UberShaderVariantTelemetry.TryParseVariantHash(generatedSource, out ulong parsedHash).ShouldBeTrue();
        parsedHash.ShouldBe(variantHash);
    }

    [Test]
    public void RequestUberVariantRebuild_InlinesStaticLiteralWithoutRewritingProtectedTokens()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            #define COLOR_NAME _Color
            // _Color should stay in line comments.
            /* _Color should stay in block comments. */
            //@property(name="_Color", display="Color", mode=static)
            uniform vec4 _Color;
            vec4 _ColorTint;
            struct Payload { vec4 _Color; };
            void main()
            {
                vec4 baseColor = _Color;
                vec4 tint = _ColorTint;
                Payload payload;
                vec4 field = payload._Color;
                vec3 rgb = _Color.rgb;
            }
            """,
            new ShaderVector4(new Vector4(0.3f, 0.4f, 0.5f, 1.0f), "_Color"));

        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);

        string generatedSource = GetFragmentSource(material);
        generatedSource.ShouldNotContain("uniform vec4 _Color;");
        generatedSource.ShouldNotContain("#define _Color ");
        generatedSource.ShouldContain("#define COLOR_NAME _Color");
        generatedSource.ShouldContain("// _Color should stay in line comments.");
        generatedSource.ShouldContain("/* _Color should stay in block comments. */");
        generatedSource.ShouldContain("vec4 _ColorTint;");
        generatedSource.ShouldContain("struct Payload { vec4 _Color; };");
        generatedSource.ShouldContain("vec4 baseColor = vec4(0.3, 0.4, 0.5, 1.0);");
        generatedSource.ShouldContain("vec4 tint = _ColorTint;");
        generatedSource.ShouldContain("vec4 field = payload._Color;");
        generatedSource.ShouldContain("vec3 rgb = (vec4(0.3, 0.4, 0.5, 1.0)).rgb;");
    }

    [Test]
    public void RequestUberVariantRebuild_UnauthoredFeatureUsesManifestDefault()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            //@feature(id="stylized-shading", name="Stylized Lighting", default=off)
            #ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
            //@property(name="_LightingMode", display="Lighting Mode", mode=static)
            uniform int _LightingMode;
            #endif
            """);

        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);

        string generatedSource = material.GetShader(EShaderType.Fragment)!.Source?.Text ?? string.Empty;
        generatedSource.ShouldNotContain("#define XRENGINE_UBER_DISABLE_STYLIZED_SHADING 1");
        generatedSource.ShouldNotContain("uniform int _LightingMode;");
        material.ActiveUberVariant.EnabledFeatures.ShouldNotContain("stylized-shading");
    }

    [Test]
    public void EnsureUberVariantPreparedForRendering_ActivatesAuthoredFeatureFromCanonicalFallback()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            #define XRENGINE_UBER_DISABLE_STYLIZED_SHADING 1
            //@feature(id="stylized-shading", name="Stylized Lighting", default=off)
            #ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
            //@property(name="_LightingMode", display="Lighting Mode", mode=static)
            uniform int _LightingMode;
            int ResolveLightingMode() { return _LightingMode; }
            #endif
            """,
            new ShaderInt(5, "_LightingMode"));

        material.EnsureUberStateInitialized();
        material.SetUberFeatureEnabled("stylized-shading", true).ShouldBeTrue();

        material.EnsureUberVariantPreparedForRendering().ShouldBeTrue(material.UberVariantStatus.FailureReason);

        string generatedSource = GetFragmentSource(material);
        generatedSource.ShouldContain("XRENGINE_UBER_GENERATED_VARIANT");
        generatedSource.ShouldNotContain("#define XRENGINE_UBER_DISABLE_STYLIZED_SHADING 1");
        generatedSource.ShouldNotContain("#define _LightingMode ");
        generatedSource.ShouldContain("int ResolveLightingMode() { return 5; }");
        material.ActiveUberVariant.EnabledFeatures.ShouldContain("stylized-shading");
    }

    [Test]
    public void PrepareUberVariantImmediately_DoesNotReassignAlreadyActiveVariant()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            //@property(name="_Color", display="Color", mode=static)
            uniform vec4 _Color;
            vec4 ResolveColor() { return _Color; }
            """,
            new ShaderVector4(new Vector4(0.8f, 0.7f, 0.6f, 1.0f), "_Color"));

        material.PrepareUberVariantImmediately().ShouldBeTrue(material.UberVariantStatus.FailureReason);
        XRShader activeShader = material.GetShader(EShaderType.Fragment).ShouldNotBeNull();
        long shaderStateRevision = material.ShaderStateRevision;

        material.PrepareUberVariantImmediately().ShouldBeTrue(material.UberVariantStatus.FailureReason);

        material.GetShader(EShaderType.Fragment).ShouldBeSameAs(activeShader);
        material.ShaderStateRevision.ShouldBe(shaderStateRevision);
    }

    [Test]
    public void IsUberVariantReadyForRendering_AcceptsCoherentActiveStatusWhenMarkerIsUnavailable()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            //@property(name="_Color", display="Color", mode=static)
            uniform vec4 _Color;
            """);

        material.SetActiveUberVariant(new UberMaterialVariantBindingState
        {
            VariantHash = 42,
            StaticProperties = ["_Color=vec4(1.0, 1.0, 1.0, 1.0)"],
        });
        material.SetUberVariantStatus(new UberMaterialVariantStatus
        {
            Stage = EUberMaterialVariantStage.Active,
            RequestedVariantHash = 42,
            ActiveVariantHash = 42,
        });

        material.IsUberVariantReadyForRendering().ShouldBeTrue();

        material.RequestUberVariantPreparationIfNeeded();

        material.UberVariantStatus.Stage.ShouldBe(EUberMaterialVariantStage.Active);
    }

    [Test]
    public void DefaultForwardPlusUberParameters_DoNotEmitWithoutAuthoredStrength()
    {
        ShaderVar[] parameters = global::XREngine.ModelImporter.CreateDefaultForwardPlusUberShaderParameters();
        ShaderFloat emissionStrength = parameters.OfType<ShaderFloat>().Single(x => x.Name == "_EmissionStrength");

        emissionStrength.Value.ShouldBe(0.0f);
    }

    [Test]
    public void RequestUberVariantRebuild_AnimatedPropertyStaysAsUniform()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            //@property(name="_Color", display="Color", mode=static)
            uniform vec4 _Color;
            vec4 ResolveColor() { return _Color; }
            """,
            new ShaderVector4(new Vector4(0.8f, 0.7f, 0.6f, 1.0f), "_Color"));

        material.EnsureUberStateInitialized();
        material.SetUberPropertyMode("_Color", EShaderUiPropertyMode.Animated).ShouldBeTrue();

        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);

        string generatedSource = material.GetShader(EShaderType.Fragment)!.Source?.Text ?? string.Empty;
        generatedSource.ShouldContain("uniform vec4 _Color;");
        generatedSource.ShouldNotContain("#define _Color ");
        material.ActiveUberVariant.AnimatedProperties.ShouldContain("_Color");
    }

    [Test]
    public void SetUberPropertyMode_AnimatedBackToStatic_CapturesCurrentRuntimeValue()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            //@property(name="_Color", display="Color", mode=static)
            uniform vec4 _Color;
            vec4 ResolveColor() { return _Color; }
            """,
            new ShaderVector4(new Vector4(0.8f, 0.7f, 0.6f, 1.0f), "_Color"));

        material.EnsureUberStateInitialized();
        material.SetUberPropertyMode("_Color", EShaderUiPropertyMode.Animated).ShouldBeTrue();

        ShaderVector4 parameter = material.Parameter<ShaderVector4>("_Color")!;
        parameter.Value = new Vector4(0.2f, 0.4f, 0.6f, 1.0f);

        material.SetUberPropertyMode("_Color", EShaderUiPropertyMode.Static).ShouldBeTrue();
        material.UberAuthoredState.GetProperty("_Color")?.StaticLiteral.ShouldBe("vec4(0.2, 0.4, 0.6, 1.0)");

        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);

        string generatedSource = material.GetShader(EShaderType.Fragment)!.Source?.Text ?? string.Empty;
        generatedSource.ShouldNotContain("#define _Color ");
        generatedSource.ShouldContain("vec4 ResolveColor() { return vec4(0.2, 0.4, 0.6, 1.0); }");
    }

    [Test]
    public void EnsureUberStateInitialized_LegacyGeneratedFragmentRestoresCanonicalShader()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"xrengine-uber-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string shaderPath = Path.Combine(tempDirectory, "UberShader.frag");
            File.WriteAllText(shaderPath,
                """
                #version 450 core
                //@property(name="_Color", display="Color", mode=static)
                uniform vec4 _Color;
                """);

            XRMaterial material = CreateUberMaterial(
                """
                #version 450 core
                // XRENGINE_UBER_GENERATED_VARIANT
                #define _Color vec4(1.0, 0.0, 0.0, 1.0)
                """,
                new ShaderVector4(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "_Color"));

            material.GetShader(EShaderType.Fragment)!.Source.FilePath = shaderPath;
            material.EnsureUberStateInitialized();

            string activeSource = material.GetShader(EShaderType.Fragment)!.Source?.Text ?? string.Empty;
            activeSource.ShouldContain("uniform vec4 _Color;");
            activeSource.ShouldNotContain("XRENGINE_UBER_GENERATED_VARIANT");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void SetUberFeatureEnabled_PrimesFeatureDefaultsForVisibleRuntimeBehavior()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            //@feature(id="stylized-shading", name="Stylized Lighting", default=off)
            #ifndef XRENGINE_UBER_DISABLE_STYLIZED_SHADING
            //@property(name="_LightingMode", display="Lighting Mode", mode=static)
            uniform int _LightingMode;
            //@property(name="_ToonRamp", display="Ramp Texture", slot=texture)
            uniform sampler2D _ToonRamp;
            #endif
            //@feature(id="rim-lighting", name="Rim Lighting", default=off)
            #ifndef XRENGINE_UBER_DISABLE_RIM_LIGHTING
            //@property(name="_RimLightColor", display="Rim Color", mode=static, default="vec4(1.0, 1.0, 1.0, 1.0)")
            uniform vec4 _RimLightColor;
            //@property(name="_RimMask", display="Rim Mask", slot=texture)
            uniform sampler2D _RimMask;
            #endif
            //@feature(id="glitter", name="Glitter", default=off)
            #ifndef XRENGINE_UBER_DISABLE_GLITTER
            //@property(name="_GlitterMask", display="Glitter Mask", slot=texture)
            uniform sampler2D _GlitterMask;
            #endif
            """,
            new ShaderInt(6, "_LightingMode"));

        material.EnsureUberStateInitialized();

        material.SetUberFeatureEnabled("stylized-shading", true).ShouldBeTrue();
        material.SetUberFeatureEnabled("rim-lighting", true).ShouldBeTrue();
        material.SetUberFeatureEnabled("glitter", true).ShouldBeTrue();

        material.Parameter<ShaderInt>("_LightingMode")?.Value.ShouldBe(5);
        material.Parameter<ShaderVector4>("_RimLightColor")?.Value.ShouldBe(new Vector4(1.0f, 1.0f, 1.0f, 1.0f));

        XRTexture2D rimMask = material.Textures.OfType<XRTexture2D>().Single(texture => texture.SamplerName == "_RimMask");
        XRTexture2D glitterMask = material.Textures.OfType<XRTexture2D>().Single(texture => texture.SamplerName == "_GlitterMask");
        XRTexture2D toonRamp = material.Textures.OfType<XRTexture2D>().Single(texture => texture.SamplerName == "_ToonRamp");

        rimMask.Mipmaps[0].Data!.GetBytes().ShouldBe([255, 255, 255, 255]);
        glitterMask.Mipmaps[0].Data!.GetBytes().ShouldBe([255, 255, 255, 255]);
        toonRamp.Mipmaps[0].Data!.GetBytes().ShouldBe([255, 255, 255, 255]);
    }

    [Test]
    public void UberVariantTelemetry_SnapshotAggregatesPreparationAdoptionAndBackendTiming()
    {
        UberShaderVariantTelemetry.ResetForTests();

        try
        {
            UberShaderVariantTelemetry.RecordRequest();
            UberShaderVariantTelemetry.RecordRequest();
            UberShaderVariantTelemetry.RecordSuccess(new UberMaterialVariantStatus
            {
                CacheHit = true,
                PreparationMilliseconds = 10.0,
                AdoptionMilliseconds = 2.0,
                GeneratedSourceLength = 120,
            });
            UberShaderVariantTelemetry.RecordSuccess(new UberMaterialVariantStatus
            {
                CacheHit = false,
                PreparationMilliseconds = 30.0,
                AdoptionMilliseconds = 6.0,
                GeneratedSourceLength = 280,
            });
            UberShaderVariantTelemetry.RecordFailure();

            UberShaderVariantTelemetry.Snapshot snapshot = UberShaderVariantTelemetry.GetSnapshot();

            snapshot.RequestCount.ShouldBe(2);
            snapshot.SuccessCount.ShouldBe(2);
            snapshot.FailureCount.ShouldBe(1);
            snapshot.CacheHitCount.ShouldBe(1);
            snapshot.CacheHitRate.ShouldBe(0.5, 0.0001);
            snapshot.AveragePreparationMilliseconds.ShouldBe(20.0, 0.0001);
            snapshot.AverageAdoptionMilliseconds.ShouldBe(4.0, 0.0001);
            snapshot.AverageGeneratedSourceBytes.ShouldBe(200.0, 0.0001);
        }
        finally
        {
            UberShaderVariantTelemetry.ResetForTests();
        }
    }

    [Test]
    public void RequestUberVariantRebuild_DisabledFeatureExcludesSamplerFromCount_AndEmitsGuardDefine()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            //@feature(id="emission", name="Emission", default=on)
            #ifndef XRENGINE_UBER_DISABLE_EMISSION
            //@property(name="_EmissionMap", display="Emission Map", slot=texture)
            uniform sampler2D _EmissionMap;
            #endif
            //@feature(id="matcap", name="Matcap", default=on)
            #ifndef XRENGINE_UBER_DISABLE_MATCAP
            //@property(name="_MatcapMap", display="Matcap Map", slot=texture)
            uniform sampler2D _MatcapMap;
            #endif
            //@property(name="_Color", display="Color", mode=animated)
            uniform vec4 _Color;
            """);

        material.EnsureUberStateInitialized();
        material.SetUberFeatureEnabled("emission", false).ShouldBeTrue();

        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);

        material.UberVariantStatus.SamplerCount.ShouldBe(1, customMessage: "disabled emission sampler must not contribute to SamplerCount");
        material.ActiveUberVariant.EnabledFeatures.ShouldNotContain("emission");
        material.ActiveUberVariant.EnabledFeatures.ShouldContain("matcap");

        string generatedSource = material.GetShader(EShaderType.Fragment)!.Source?.Text ?? string.Empty;
        generatedSource.ShouldNotContain("#define XRENGINE_UBER_DISABLE_EMISSION 1");
        generatedSource.ShouldNotContain("#define XRENGINE_UBER_DISABLE_MATCAP 1");
        generatedSource.ShouldNotContain("uniform sampler2D _EmissionMap;");
        generatedSource.ShouldContain("uniform sampler2D _MatcapMap;");
    }

    [Test]
    public void RequestUberVariantRebuild_PrunesKnownFeatureAndPipelineConditionals()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            //@feature(id="emission", name="Emission", default=on)
            #ifndef XRENGINE_UBER_DISABLE_EMISSION
            //@property(name="_EmissionMap", display="Emission Map", slot=texture)
            uniform sampler2D _EmissionMap;
            vec3 SampleEmission() { return texture(_EmissionMap, vec2(0.0)).rgb; }
            #endif
            #ifdef XRENGINE_UBER_DISABLE_EMISSION
            vec3 EmissionValue() { return vec3(0.0); }
            #else
            vec3 EmissionValue() { return SampleEmission(); }
            #endif
            #if !defined(XRENGINE_DEPTH_NORMAL_PREPASS) && !defined(XRENGINE_SHADOW_CASTER_PASS)
            layout(location = 0) out vec4 FragColor;
            #else
            layout(location = 0) out vec2 Normal;
            #endif
            """);

        material.EnsureUberStateInitialized();
        material.SetUberFeatureEnabled("emission", false).ShouldBeTrue();

        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);

        string generatedSource = material.GetShader(EShaderType.Fragment)!.Source?.Text ?? string.Empty;
        generatedSource.ShouldNotContain("#define XRENGINE_UBER_DISABLE_EMISSION 1");
        generatedSource.ShouldNotContain("uniform sampler2D _EmissionMap;");
        generatedSource.ShouldNotContain("SampleEmission");
        generatedSource.ShouldContain("vec3 EmissionValue() { return vec3(0.0); }");
        generatedSource.ShouldContain("layout(location = 0) out vec4 FragColor;");
        generatedSource.ShouldNotContain("layout(location = 0) out vec2 Normal;");
    }

    [Test]
    public void RequestUberVariantRebuild_PrunesDisabledFeatureIncludesAndUnusedIncludeDeclarations()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"xrengine-uber-includes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string shaderPath = Path.Combine(tempDirectory, "UberShader.frag");
            File.WriteAllText(Path.Combine(tempDirectory, "common.glsl"),
                """
                const float KEEP_SCALE = 1.0;
                vec3 UsedHelper(vec3 value) { return value * KEEP_SCALE; }
                vec3 UnusedHelper(vec3 value) { return UnusedLeaf(value); }
                vec3 UnusedLeaf(vec3 value) { return value * 9.0; }
                uniform sampler2D _UnusedIncludeSampler;
                """);
            File.WriteAllText(Path.Combine(tempDirectory, "dissolve.glsl"),
                """
                vec3 ApplyDissolve(vec3 value) { return value * 0.5; }
                """);
            File.WriteAllText(shaderPath,
                """
                #version 450 core
                #include "common.glsl"
                //@property(name="_Color", display="Color", mode=static)
                uniform vec4 _Color;
                //@feature(id="dissolve", name="Dissolve", default=off)
                #ifndef XRENGINE_UBER_DISABLE_DISSOLVE
                #include "dissolve.glsl"
                #endif
                void main()
                {
                    vec3 color = UsedHelper(_Color.rgb);
                }
                """);

            XRMaterial material = CreateUberMaterialFromFile(
                shaderPath,
                new ShaderVector4(new Vector4(0.8f, 0.7f, 0.6f, 1.0f), "_Color"));

            material.EnsureUberStateInitialized();
            material.PrepareUberVariantImmediately().ShouldBeTrue(material.UberVariantStatus.FailureReason);

            string generatedSource = GetFragmentSource(material);
            generatedSource.ShouldContain("XRENGINE_UBER_GENERATED_VARIANT");
            generatedSource.ShouldContain("UsedHelper");
            generatedSource.ShouldContain("KEEP_SCALE");
            generatedSource.ShouldNotContain("#define XRENGINE_UBER_DISABLE_DISSOLVE 1");
            generatedSource.ShouldNotContain("#define _Color ");
            generatedSource.ShouldContain("(vec4(0.8, 0.7, 0.6, 1.0)).rgb");
            generatedSource.ShouldNotContain("UnusedHelper");
            generatedSource.ShouldNotContain("UnusedLeaf");
            generatedSource.ShouldNotContain("_UnusedIncludeSampler");
            generatedSource.ShouldNotContain("ApplyDissolve");
            generatedSource.ShouldNotContain("BEGIN INCLUDE");
            generatedSource.ShouldNotContain("END INCLUDE");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void RequestUberVariantRebuild_PrunesUnusedMainHelpersBeforeSnippetReferencesKeepThemAlive()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"xrengine-uber-main-dce-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string shaderPath = Path.Combine(tempDirectory, "UberShader.frag");
            File.WriteAllText(Path.Combine(tempDirectory, "common.glsl"),
                """
                vec3 UsedSnippetHelper(vec3 value) { return value; }
                vec3 KeptOnlyByUnusedMainHelper(vec3 value) { return value * 9.0; }
                """);
            File.WriteAllText(shaderPath,
                """
                #version 450 core
                #include "common.glsl"
                //@property(name="_Color", display="Color", mode=static)
                uniform vec4 _Color;
                vec3 UnusedMainHelper(vec3 color) { return KeptOnlyByUnusedMainHelper(color); }
                void main()
                {
                    vec3 color = UsedSnippetHelper(_Color.rgb);
                }
                """);

            XRMaterial material = CreateUberMaterialFromFile(
                shaderPath,
                new ShaderVector4(new Vector4(0.8f, 0.7f, 0.6f, 1.0f), "_Color"));

            material.EnsureUberStateInitialized();
            material.RequestUberVariantRebuild();
            WaitForActiveUberVariant(material);

            string generatedSource = GetFragmentSource(material);
            generatedSource.ShouldContain("UsedSnippetHelper");
            generatedSource.ShouldContain("void main()");
            generatedSource.ShouldNotContain("UnusedMainHelper");
            generatedSource.ShouldNotContain("KeptOnlyByUnusedMainHelper");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void RequestUberVariantRebuild_PrunesStaticIfBlocksBeforeDce()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            #define MODE_CUTOUT 1
            const float EPSILON = 0.0001;
            //@property(name="_Color", display="Color", mode=static)
            uniform vec4 _Color;
            void UsedPath() { }
            void RemovedPath() { }
            void main()
            {
                if (0 == MODE_CUTOUT) {
                    RemovedPath();
                } else {
                    UsedPath();
                }

                if (abs(1.0) > EPSILON) {
                    UsedPath();
                }
            }
            """,
            new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_Color"));

        material.PrepareUberVariantImmediately().ShouldBeTrue(material.UberVariantStatus.FailureReason);

        string generatedSource = GetFragmentSource(material);
        generatedSource.ShouldContain("UsedPath");
        generatedSource.ShouldNotContain("RemovedPath");
        generatedSource.ShouldNotContain("0 == MODE_CUTOUT");
        generatedSource.ShouldNotContain("abs(1.0) > EPSILON");
    }

    [Test]
    public void PrepareVariant_RealUberForwardSnippetRetainsReferencedRuntimeGlobals()
    {
        string shaderPath = ResolveWorkspacePath(Path.Combine("Build", "CommonAssets", "Shaders", "Uber", "UberShader.frag"));
        XRMaterial material = CreateUberMaterialFromFile(shaderPath, ModelImporter.CreateDefaultForwardPlusUberShaderParameters());
        material.RenderOptions = ModelImporter.CreateForwardPlusUberShaderRenderOptions();
        material.Parameter<ShaderFloat>("_ForwardPbrResourcesEnabled")?.SetValue(1.0f);

        material.EnsureUberStateInitialized();
        material.PrepareUberVariantImmediately().ShouldBeTrue(material.UberVariantStatus.FailureReason);

        string generatedSource = GetFragmentSource(material);
        generatedSource.ShouldContain("XRENGINE_UBER_GENERATED_VARIANT");
        generatedSource.ShouldContain("const vec2 XRENGINE_ShadowPoissonDisk[16]");
        generatedSource.ShouldContain("const vec3 XRENGINE_ShadowCubeKernel[20]");
        generatedSource.ShouldContain("layout(location = 22) in float FragViewIndex;");
        generatedSource.ShouldContain("mat4 XRENGINE_ResolvedForwardViewMatrix");
        generatedSource.ShouldContain("vec3 XRENGINE_ForwardShadowDebugColor");
        generatedSource.ShouldContain("layout(binding = 9) uniform sampler2DArray DirectionalShadowAtlas;");
        generatedSource.ShouldContain("layout(binding = 6) uniform sampler2D BRDF;");
        generatedSource.ShouldNotContain("BEGIN SNIPPET");
        generatedSource.ShouldNotContain("BEGIN INCLUDE");
    }

    [Test]
    public void PrepareVariant_RealUberForwardLighting_PrunesShadowAndPbrResourceFamiliesWhenExplicitlyDisabled()
    {
        string shaderPath = ResolveWorkspacePath(Path.Combine("Build", "CommonAssets", "Shaders", "Uber", "UberShader.frag"));
        XRMaterial material = CreateUberMaterialFromFile(shaderPath, ModelImporter.CreateDefaultForwardPlusUberShaderParameters());
        material.RenderOptions = ModelImporter.CreateForwardPlusUberShaderRenderOptions();
        material.Parameter<ShaderFloat>("_ForwardShadowsEnabled")?.SetValue(0.0f);
        material.Parameter<ShaderFloat>("_ForwardContactShadowsEnabled")?.SetValue(0.0f);
        material.Parameter<ShaderFloat>("_ForwardPbrResourcesEnabled")?.SetValue(0.0f);

        material.EnsureUberStateInitialized();
        material.PrepareUberVariantImmediately().ShouldBeTrue(material.UberVariantStatus.FailureReason);

        string generatedSource = GetFragmentSource(material);
        generatedSource.ShouldContain("XRENGINE_UBER_GENERATED_VARIANT");
        generatedSource.ShouldContain("DirLightCount");
        generatedSource.ShouldContain("DirectionalLights[]");
        generatedSource.ShouldContain("XRENGINE_CalculateDirectPbrLightWithViewDir");
        generatedSource.ShouldNotContain("const vec2 XRENGINE_ShadowPoissonDisk[16]");
        generatedSource.ShouldNotContain("const vec3 XRENGINE_ShadowCubeKernel[20]");
        generatedSource.ShouldNotContain("layout(binding = 9) uniform sampler2DArray DirectionalShadowAtlas;");
        generatedSource.ShouldNotContain("layout(binding = 19) uniform samplerCube PointLightShadowMaps");
        generatedSource.ShouldNotContain("ForwardContactDepthView");
        generatedSource.ShouldNotContain("layout(binding = 6) uniform sampler2D BRDF;");
        generatedSource.ShouldNotContain("layout(binding = 7) uniform sampler2DArray IrradianceArray;");
        generatedSource.ShouldNotContain("layout(std430, binding = 0) readonly buffer LightProbePositions");
        generatedSource.ShouldNotContain("XRENGINE_ResolveProbeWeightsGrid");
        generatedSource.Length.ShouldBeLessThan(180_000);
    }

    [Test]
    public void PrepareVariant_RealUberWithoutForwardRequirements_PrunesForwardLightingAndDefaultTextureFeatures()
    {
        string shaderPath = ResolveWorkspacePath(Path.Combine("Build", "CommonAssets", "Shaders", "Uber", "UberShader.frag"));
        XRMaterial material = CreateUberMaterialFromFile(shaderPath, ModelImporter.CreateDefaultForwardPlusUberShaderParameters());

        material.EnsureUberStateInitialized();
        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);

        string generatedSource = GetFragmentSource(material);
        generatedSource.ShouldContain("XRENGINE_UBER_GENERATED_VARIANT");
        generatedSource.ShouldContain("fragData.finalColor = fragData.baseColor;");
        generatedSource.ShouldNotContain("layout(binding = 9) uniform sampler2DArray DirectionalShadowAtlas;");
        generatedSource.ShouldNotContain("layout(binding = 6) uniform sampler2D BRDF;");
        generatedSource.ShouldNotContain("XRENGINE_CalculateAmbientPbr");
        generatedSource.ShouldNotContain("uniform sampler2D _BumpMap;");
        generatedSource.ShouldNotContain("uniform sampler2D _AlphaMask;");
        generatedSource.ShouldNotContain("applyDetailNormal");
        generatedSource.ShouldNotContain("sampleMaterialAmbientOcclusion");
        generatedSource.ShouldNotContain("calculateStylizedAdditionalLighting");
        material.ActiveUberVariant.EnabledFeatures.ShouldNotContain("alpha-masks");
        material.ActiveUberVariant.EnabledFeatures.ShouldNotContain("normal-map");
    }

    [Test]
    public void PrepareVariant_UsesDependencyAwareResolvedSourceCache()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"xrengine-uber-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string shaderPath = WriteIncludeBackedUberShader(tempDirectory, "1.0");
            XRMaterial material = CreateUberMaterialFromFile(shaderPath, new ShaderFloat(2.0f, "_Scale"));

            UberShaderVariantBuilder.PreparedUberVariant first = PrepareVariantForTests(material);
            UberShaderVariantBuilder.CacheStats afterFirst = UberShaderVariantBuilder.GetCacheStatsForTests();

            UberShaderVariantBuilder.PreparedUberVariant second = PrepareVariantForTests(material);
            UberShaderVariantBuilder.CacheStats afterSecond = UberShaderVariantBuilder.GetCacheStatsForTests();

            first.CacheHit.ShouldBeFalse();
            second.CacheHit.ShouldBeTrue();
            second.FragmentShader.ShouldBeSameAs(first.FragmentShader);
            afterFirst.ResolvedSourceMisses.ShouldBe(1);
            afterSecond.ResolvedSourceMisses.ShouldBe(afterFirst.ResolvedSourceMisses);
            afterSecond.ResolvedSourceHits.ShouldBeGreaterThan(afterFirst.ResolvedSourceHits);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void PrepareVariant_ResolvedSourceCacheInvalidatesWhenIncludeChanges()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"xrengine-uber-cache-invalidate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string shaderPath = WriteIncludeBackedUberShader(tempDirectory, "1.0");
            XRMaterial material = CreateUberMaterialFromFile(shaderPath, new ShaderFloat(2.0f, "_Scale"));

            UberShaderVariantBuilder.PreparedUberVariant first = PrepareVariantForTests(material);
            (first.FragmentShader.Source.Text ?? string.Empty).ShouldContain("const float KEEP_SCALE = 1.0;");

            Thread.Sleep(20);
            File.WriteAllText(Path.Combine(tempDirectory, "common.glsl"),
                """
                const float KEEP_SCALE = 22.0;
                """);

            UberShaderVariantBuilder.PreparedUberVariant second = PrepareVariantForTests(material);
            UberShaderVariantBuilder.CacheStats stats = UberShaderVariantBuilder.GetCacheStatsForTests();

            second.FragmentShader.ShouldNotBeSameAs(first.FragmentShader);
            (second.FragmentShader.Source.Text ?? string.Empty).ShouldContain("const float KEEP_SCALE = 22.0;");
            stats.ResolvedSourceMisses.ShouldBe(2);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void PrepareVariant_ResolvedSourceCacheInvalidatesWhenDirectSourceChanges()
    {
        TextFile source = TextFile.FromText(
            """
            #version 450 core
            //@property(name="_Scale", display="Scale", mode=static)
            uniform float _Scale;
            float ResolveScale() { return _Scale; }
            """);
        source.FilePath = "UberShader.frag";
        source.Name = "UberShader.frag";
        XRMaterial material = CreateUberMaterial(source, new ShaderFloat(2.0f, "_Scale"));

        UberShaderVariantBuilder.PreparedUberVariant first = PrepareVariantForTests(material);
        (first.FragmentShader.Source.Text ?? string.Empty).ShouldContain("return 2.0;");

        source.Text =
            """
            #version 450 core
            //@property(name="_Scale", display="Scale", mode=static)
            uniform float _Scale;
            float ResolveScale() { return _Scale * 3.0; }
            """;

        UberShaderVariantBuilder.PreparedUberVariant second = PrepareVariantForTests(material);
        UberShaderVariantBuilder.CacheStats stats = UberShaderVariantBuilder.GetCacheStatsForTests();

        second.FragmentShader.ShouldNotBeSameAs(first.FragmentShader);
        second.Request.SourceVersion.ShouldNotBe(first.Request.SourceVersion);
        (second.FragmentShader.Source.Text ?? string.Empty).ShouldContain("return 2.0 * 3.0;");
        stats.ResolvedSourceMisses.ShouldBe(2);
    }

    [Test]
    public void PrepareVariant_VertexPermutationHashInvalidatesWhenVertexIncludeChanges()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"xrengine-uber-vertex-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string vertexPath = WriteIncludeBackedVertexShader(tempDirectory, "1.0");
            XRMaterial material = CreateUberMaterial(
                """
                #version 450 core
                //@property(name="_Scale", display="Scale", mode=static)
                uniform float _Scale;
                float ResolveScale() { return _Scale; }
                """,
                new ShaderFloat(2.0f, "_Scale"));
            material.SetShader(EShaderType.Vertex, new XRShader(EShaderType.Vertex, LoadTextFile(vertexPath)), coerceShaderType: true);

            UberShaderVariantBuilder.PreparedUberVariant first = PrepareVariantForTests(material);

            Thread.Sleep(20);
            File.WriteAllText(Path.Combine(tempDirectory, "vertex-common.glsl"),
                """
                const float VERTEX_SCALE = 9.0;
                """);

            UberShaderVariantBuilder.PreparedUberVariant second = PrepareVariantForTests(material);

            second.Request.VertexPermutationHash.ShouldNotBe(first.Request.VertexPermutationHash);
            second.Request.VariantHash.ShouldNotBe(first.Request.VariantHash);
            second.FragmentShader.ShouldNotBeSameAs(first.FragmentShader);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void PrepareVariant_ConcurrentMissesShareOneSourceResolve()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"xrengine-uber-cache-concurrent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string shaderPath = WriteIncludeBackedUberShader(tempDirectory, "1.0");
            XRMaterial material = CreateUberMaterialFromFile(shaderPath, new ShaderFloat(2.0f, "_Scale"));
            material.EnsureUberStateInitialized();
            material.TryGetUberMaterialState(out XRShader? shader, out ShaderUiManifest manifest).ShouldBeTrue();
            shader.ShouldNotBeNull();

            const int taskCount = 8;
            Task[] tasks = new Task[taskCount];
            for (int i = 0; i < taskCount; i++)
            {
                tasks[i] = Task.Run(() =>
                    UberShaderVariantBuilder.PrepareVariant(material, shader!, manifest));
            }

            Task.WaitAll(tasks);

            UberShaderVariantBuilder.CacheStats stats = UberShaderVariantBuilder.GetCacheStatsForTests();
            stats.ResolvedSourceMisses.ShouldBe(1);
            stats.ResolvedSourceHits.ShouldBeGreaterThanOrEqualTo(taskCount - 1);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void RequestUberVariantRebuild_FailedPreparationRestoresCanonicalSourceAndReportsFailure()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"xrengine-uber-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string shaderPath = Path.Combine(tempDirectory, "UberShader.frag");
            const string canonicalSource =
                """
                #version 450 core
                //@property(name="_Color", display="Color", mode=static)
                uniform vec4 _Color;
                """;
            File.WriteAllText(shaderPath, canonicalSource);

            XRMaterial material = CreateUberMaterial(
                """
                #version 450 core
                // XRENGINE_UBER_GENERATED_VARIANT
                #define _Color vec4(1.0, 0.0, 0.0, 1.0)
                """,
                new ShaderVector4(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "_Color"));

            material.GetShader(EShaderType.Fragment)!.Source.FilePath = shaderPath;
            material.EnsureUberStateInitialized();

            string restoredSource = material.GetShader(EShaderType.Fragment)!.Source?.Text ?? string.Empty;
            restoredSource.ShouldContain("uniform vec4 _Color;");
            restoredSource.ShouldNotContain("XRENGINE_UBER_GENERATED_VARIANT");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void SetUberPropertyMode_StaticAnimatedRoundTrip_PreservesValueAndRestoresUniformHookup()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            //@property(name="_Color", display="Color", mode=static)
            uniform vec4 _Color;
            vec4 ResolveColor() { return _Color; }
            """,
            new ShaderVector4(new Vector4(0.8f, 0.7f, 0.6f, 1.0f), "_Color"));

        material.EnsureUberStateInitialized();

        material.SetUberPropertyMode("_Color", EShaderUiPropertyMode.Animated).ShouldBeTrue();
        ShaderVector4 parameter = material.Parameter<ShaderVector4>("_Color")!;
        parameter.Value = new Vector4(0.1f, 0.2f, 0.3f, 0.4f);

        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);
        GetFragmentSource(material).ShouldContain("uniform vec4 _Color;");

        material.SetUberPropertyMode("_Color", EShaderUiPropertyMode.Static).ShouldBeTrue();
        material.UberAuthoredState.GetProperty("_Color")?.StaticLiteral.ShouldBe("vec4(0.1, 0.2, 0.3, 0.4)");

        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);
        string staticSource = GetFragmentSource(material);
        staticSource.ShouldNotContain("#define _Color ");
        staticSource.ShouldContain("vec4 ResolveColor() { return vec4(0.1, 0.2, 0.3, 0.4); }");

        material.SetUberPropertyMode("_Color", EShaderUiPropertyMode.Animated).ShouldBeTrue();
        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);

        string finalSource = GetFragmentSource(material);
        finalSource.ShouldContain("uniform vec4 _Color;");
        finalSource.ShouldNotContain("#define _Color ");
        finalSource.ShouldContain("vec4 ResolveColor() { return _Color; }");
        material.Parameter<ShaderVector4>("_Color")!.Value.ShouldBe(new Vector4(0.1f, 0.2f, 0.3f, 0.4f));
    }

    [Test]
    public void TryGetUberMaterialState_AfterConstantBake_ReturnsCanonicalManifestWithAuthorableProperty()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            //@property(name="_Color", display="Color", mode=constant)
            uniform vec4 _Color;
            """,
            new ShaderVector4(new Vector4(0.2f, 0.3f, 0.4f, 1.0f), "_Color"));

        material.EnsureUberStateInitialized();
        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);

        material.TryGetUberMaterialState(out XRShader? canonicalFragmentShader, out ShaderUiManifest manifest).ShouldBeTrue();
        canonicalFragmentShader.ShouldNotBeNull();
        Path.GetFileName(canonicalFragmentShader!.Source?.FilePath ?? canonicalFragmentShader.FilePath)
            .ShouldBe("UberShader.frag");
        manifest.PropertyLookup.ShouldContainKey("_Color");
    }

    [Test]
    public void RequestedUberVariantHash_DiffersAcrossFeatureMaskAndPropertyMode()
    {
        const string source =
            """
            #version 450 core
            //@feature(id="emission", name="Emission", default=on)
            #ifndef XRENGINE_UBER_DISABLE_EMISSION
            //@property(name="_EmissionColor", display="Emission Color", mode=static)
            uniform vec4 _EmissionColor;
            #endif
            //@property(name="_Color", display="Color", mode=static)
            uniform vec4 _Color;
            """;

        XRMaterial baseline = CreateUberMaterial(source,
            new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_Color"),
            new ShaderVector4(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), "_EmissionColor"));
        baseline.EnsureUberStateInitialized();
        baseline.RequestUberVariantRebuild();
        WaitForActiveUberVariant(baseline);
        ulong baselineHash = baseline.ActiveUberVariant.VariantHash;

        XRMaterial featureToggled = CreateUberMaterial(source,
            new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_Color"),
            new ShaderVector4(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), "_EmissionColor"));
        featureToggled.EnsureUberStateInitialized();
        featureToggled.SetUberFeatureEnabled("emission", false).ShouldBeTrue();
        featureToggled.RequestUberVariantRebuild();
        WaitForActiveUberVariant(featureToggled);
        featureToggled.ActiveUberVariant.VariantHash.ShouldNotBe(baselineHash);

        XRMaterial modeToggled = CreateUberMaterial(source,
            new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_Color"),
            new ShaderVector4(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), "_EmissionColor"));
        modeToggled.EnsureUberStateInitialized();
        modeToggled.SetUberPropertyMode("_Color", EShaderUiPropertyMode.Animated).ShouldBeTrue();
        modeToggled.RequestUberVariantRebuild();
        WaitForActiveUberVariant(modeToggled);
        modeToggled.ActiveUberVariant.VariantHash.ShouldNotBe(baselineHash);

        XRMaterial literalToggled = CreateUberMaterial(source,
            new ShaderVector4(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "_Color"),
            new ShaderVector4(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), "_EmissionColor"));
        literalToggled.EnsureUberStateInitialized();
        literalToggled.RequestUberVariantRebuild();
        WaitForActiveUberVariant(literalToggled);
        literalToggled.ActiveUberVariant.VariantHash.ShouldNotBe(baselineHash);
    }

    [Test]
    public void RequestedUberVariantHash_IsDeterministicForEquivalentInputs()
    {
        const string source =
            """
            #version 450 core
            //@feature(id="emission", name="Emission", default=on)
            #ifndef XRENGINE_UBER_DISABLE_EMISSION
            //@property(name="_EmissionColor", display="Emission Color", mode=static)
            uniform vec4 _EmissionColor;
            vec4 ResolveEmission() { return _EmissionColor; }
            #endif
            //@property(name="_Color", display="Color", mode=static)
            uniform vec4 _Color;
            vec4 ResolveColor() { return _Color; }
            """;

        XRMaterial first = CreateUberMaterial(source,
            new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_Color"),
            new ShaderVector4(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), "_EmissionColor"));
        XRMaterial second = CreateUberMaterial(source,
            new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_Color"),
            new ShaderVector4(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), "_EmissionColor"));

        first.PrepareUberVariantImmediately().ShouldBeTrue(first.UberVariantStatus.FailureReason);
        second.PrepareUberVariantImmediately().ShouldBeTrue(second.UberVariantStatus.FailureReason);

        first.ActiveUberVariant.VariantHash.ShouldBe(second.ActiveUberVariant.VariantHash);
        first.ActiveUberVariant.SourceVersion.ShouldBe(second.ActiveUberVariant.SourceVersion);
        GetFragmentSource(first).ShouldBe(GetFragmentSource(second));
    }

    [Test]
    public void UberVariantPreparationBaseline_MinimalCommonMaximalContractIsStable()
    {
        string contract = BuildUberVariantBaselineContract();
        TestContext.Out.WriteLine(contract);
        contract.ShouldBe(ExpectedUberVariantBaselineContract);
    }

    [Test]
    public void RequestUberVariantRebuild_FormatsCommonFloatStaticLiterals()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            //@property(name="_Zero", display="Zero", mode=static)
            uniform float _Zero;
            //@property(name="_One", display="One", mode=static)
            uniform float _One;
            //@property(name="_NegativeOne", display="Negative One", mode=static)
            uniform float _NegativeOne;
            //@property(name="_Half", display="Half", mode=static)
            uniform float _Half;
            float ResolveValues() { return _Zero + _One + _NegativeOne + _Half; }
            """,
            new ShaderFloat(-0.0f, "_Zero"),
            new ShaderFloat(1.0f, "_One"),
            new ShaderFloat(-1.0f, "_NegativeOne"),
            new ShaderFloat(0.5f, "_Half"));

        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);

        string generatedSource = GetFragmentSource(material);
        generatedSource.ShouldContain("return 0.0 + 1.0 + -1.0 + 0.5;");
        material.ActiveUberVariant.StaticProperties.ShouldContain("_Zero=0.0");
        material.ActiveUberVariant.StaticProperties.ShouldContain("_One=1.0");
        material.ActiveUberVariant.StaticProperties.ShouldContain("_NegativeOne=-1.0");
        material.ActiveUberVariant.StaticProperties.ShouldContain("_Half=0.5");
    }

    [Test]
    public void RequestUberVariantRebuild_CollapsesBlankLinesAfterDefineAndStaticUniformStripping()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core

            #define XRENGINE_FORWARD_WEIGHTED_OIT 1

            //@property(name="_Scale", display="Scale", mode=static)
            uniform float _Scale;


            float ResolveScale() { return _Scale; }
            """,
            new ShaderFloat(1.0f, "_Scale"));

        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);

        string generatedSource = GetFragmentSource(material);
        generatedSource.ShouldNotContain("#define XRENGINE_FORWARD_WEIGHTED_OIT 1");
        generatedSource.ShouldNotContain("uniform float _Scale;");
        generatedSource.ShouldNotContain($"{Environment.NewLine}{Environment.NewLine}{Environment.NewLine}");
    }

    [Test, Explicit("Baseline harness: runs locally to capture CPU-side preparation timings for minimal/common/maximal Uber variants.")]
    public void UberVariantPreparationBaseline_CapturesMinimalCommonMaximalTimings()
    {
        const string source =
            """
            #version 450 core
            //@feature(id="emission", name="Emission", default=on)
            #ifndef XRENGINE_UBER_DISABLE_EMISSION
            //@property(name="_EmissionColor", display="Emission", mode=static)
            uniform vec4 _EmissionColor;
            //@property(name="_EmissionMap", display="Emission Map", slot=texture)
            uniform sampler2D _EmissionMap;
            #endif
            //@feature(id="matcap", name="Matcap", default=on)
            #ifndef XRENGINE_UBER_DISABLE_MATCAP
            //@property(name="_MatcapMap", display="Matcap Map", slot=texture)
            uniform sampler2D _MatcapMap;
            #endif
            //@feature(id="parallax", name="Parallax", default=on)
            #ifndef XRENGINE_UBER_DISABLE_PARALLAX
            //@property(name="_ParallaxAmount", display="Parallax Amount", mode=static)
            uniform float _ParallaxAmount;
            #endif
            //@property(name="_Color", display="Color", mode=static)
            uniform vec4 _Color;
            """;

        (string Label, string[] DisabledFeatures)[] cases =
        [
            ("minimal", new[] { "emission", "matcap", "parallax" }),
            ("common", new[] { "parallax" }),
            ("maximal", Array.Empty<string>()),
        ];

        System.Text.StringBuilder report = new();
        report.AppendLine("variant,preparation_ms,adoption_ms,generated_source_bytes,animated_count,sampler_count,cache_hit");

        foreach ((string label, string[] disabled) in cases)
        {
            XRMaterial material = CreateUberMaterial(source,
                new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_Color"),
                new ShaderVector4(new Vector4(0.1f, 0.1f, 0.1f, 1.0f), "_EmissionColor"),
                new ShaderFloat(0.02f, "_ParallaxAmount"));

            material.EnsureUberStateInitialized();
            foreach (string featureId in disabled)
                material.SetUberFeatureEnabled(featureId, false);

            material.RequestUberVariantRebuild();
            WaitForActiveUberVariant(material);

            UberMaterialVariantStatus status = material.UberVariantStatus;
            report.AppendLine($"{label},{status.PreparationMilliseconds:F3},{status.AdoptionMilliseconds:F3},{status.GeneratedSourceLength},{status.UniformCount},{status.SamplerCount},{status.CacheHit}");
        }

        string logDirectory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Build", "Logs", "uber-variant-baselines");
        Directory.CreateDirectory(logDirectory);
        string logPath = Path.Combine(logDirectory, $"uber-variant-baseline-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
        File.WriteAllText(logPath, report.ToString());

        TestContext.Out.WriteLine($"Uber variant baseline written to {logPath}");
        TestContext.Out.WriteLine(report.ToString());
    }

    [Test]
    public void XRRenderProgramShaderMetadata_TracksVariantCompileLinkAndFailureStages()
    {
        XRRenderProgram program = new();
        const ulong variantHash = 0x1234uL;

        program.SetShaderVariantMetadata(new XRRenderProgram.ShaderProgramVariantMetadata(
            "TestVariant",
            variantHash,
            XRRenderProgram.EShaderProgramBinaryCachePolicy.BypassWhenDriverParallelCompile));

        program.ShaderMetadata.Variant.Kind.ShouldBe("TestVariant");
        program.ShaderMetadata.Variant.VariantHash.ShouldBe(variantHash);
        program.ShaderMetadata.Variant.BinaryCachePolicy.ShouldBe(XRRenderProgram.EShaderProgramBinaryCachePolicy.BypassWhenDriverParallelCompile);

        program.SetShaderBackendStatus(new XRRenderProgram.ShaderProgramBackendStatus(
            XRRenderProgram.EShaderProgramBackendStage.Compiling,
            0.0,
            0.0,
            null));
        program.ShaderMetadata.Backend.Stage.ShouldBe(XRRenderProgram.EShaderProgramBackendStage.Compiling);

        program.SetShaderBackendStatus(new XRRenderProgram.ShaderProgramBackendStatus(
            XRRenderProgram.EShaderProgramBackendStage.Linking,
            5.0,
            0.0,
            null));
        program.ShaderMetadata.Backend.Stage.ShouldBe(XRRenderProgram.EShaderProgramBackendStage.Linking);
        program.ShaderMetadata.Backend.CompileMilliseconds.ShouldBe(5.0, 0.0001);

        program.SetShaderBackendStatus(new XRRenderProgram.ShaderProgramBackendStatus(
            XRRenderProgram.EShaderProgramBackendStage.Failed,
            5.0,
            1.5,
            "link failed"));
        program.ShaderMetadata.Backend.Stage.ShouldBe(XRRenderProgram.EShaderProgramBackendStage.Failed);
        program.ShaderMetadata.Backend.CompileMilliseconds.ShouldBe(5.0, 0.0001);
        program.ShaderMetadata.Backend.LinkMilliseconds.ShouldBe(1.5, 0.0001);
        program.ShaderMetadata.Backend.FailureReason.ShouldBe("link failed");
    }

    private static XRMaterial CreateUberMaterial(string fragmentSource, params ShaderVar[] parameters)
    {
        TextFile source = TextFile.FromText(fragmentSource);
        source.FilePath = "UberShader.frag";
        source.Name = "UberShader.frag";

        return CreateUberMaterial(source, parameters);
    }

    private static XRMaterial CreateUberMaterialFromFile(string shaderPath, params ShaderVar[] parameters)
    {
        TextFile source = new(shaderPath);
        source.LoadText(shaderPath);
        return CreateUberMaterial(source, parameters);
    }

    private static XRMaterial CreateUberMaterial(TextFile source, params ShaderVar[] parameters)
    {
        XRMaterial material = new()
        {
            Parameters = parameters.Length == 0 ? [] : parameters,
        };

        material.SetShader(EShaderType.Fragment, new XRShader(EShaderType.Fragment, source), coerceShaderType: true);
        return material;
    }

    private static UberShaderVariantBuilder.PreparedUberVariant PrepareVariantForTests(XRMaterial material)
    {
        material.EnsureUberStateInitialized();
        material.TryGetUberMaterialState(out XRShader? shader, out ShaderUiManifest manifest).ShouldBeTrue();
        shader.ShouldNotBeNull();
        return UberShaderVariantBuilder.PrepareVariant(material, shader!, manifest);
    }

    private static string WriteIncludeBackedUberShader(string directory, string keepScaleLiteral)
    {
        string shaderPath = Path.Combine(directory, "UberShader.frag");
        File.WriteAllText(Path.Combine(directory, "common.glsl"),
            $$"""
            const float KEEP_SCALE = {{keepScaleLiteral}};
            """);
        File.WriteAllText(shaderPath,
            """
            #version 450 core
            #include "common.glsl"
            //@property(name="_Scale", display="Scale", mode=static)
            uniform float _Scale;
            float ResolveScale() { return KEEP_SCALE * _Scale; }
            """);
        return shaderPath;
    }

    private static string WriteIncludeBackedVertexShader(string directory, string vertexScaleLiteral)
    {
        string shaderPath = Path.Combine(directory, "UberShader.vert");
        File.WriteAllText(Path.Combine(directory, "vertex-common.glsl"),
            $$"""
            const float VERTEX_SCALE = {{vertexScaleLiteral}};
            """);
        File.WriteAllText(shaderPath,
            """
            #version 450 core
            #include "vertex-common.glsl"
            void main() { gl_Position = vec4(VERTEX_SCALE); }
            """);
        return shaderPath;
    }

    private static TextFile LoadTextFile(string path)
    {
        TextFile text = new(path);
        text.LoadText(path);
        return text;
    }

    private const string ExpectedUberVariantBaselineContract =
        """
        label,variant_hash,source_version,generated_source_length,animated_count,sampler_count,source
        minimal,0xa91986e7866aaa35,-6050354667351332811,420,0,0,#version 450 core\r\n// XRENGINE_UBER_GENERATED_VARIANT\r\n// variant-hash: 0xa91986e7866aaa35\r\n\r\n//@feature(id="emission"\, name="Emission"\, default=on)\r\n//@feature(id="matcap"\, name="Matcap"\, default=on)\r\n//@feature(id="parallax"\, name="Parallax"\, default=on)\r\n//@property(name="_Color"\, display="Color"\, mode=static)\r\nvec4 ResolveColor() { return vec4(1.0\, 1.0\, 1.0\, 1.0); }\r\nvoid main() { vec4 color = ResolveColor(); }\r\n
        common,0xeef145ce9db35dbd,-6050354667351332811,312,0,0,#version 450 core\r\n// XRENGINE_UBER_GENERATED_VARIANT\r\n// variant-hash: 0xeef145ce9db35dbd\r\n\r\n//@feature(id="parallax"\, name="Parallax"\, default=on)\r\n//@property(name="_Color"\, display="Color"\, mode=static)\r\nvec4 ResolveColor() { return vec4(1.0\, 1.0\, 1.0\, 1.0); }\r\nvoid main() { vec4 color = ResolveColor(); }\r\n
        maximal,0x4faa440a0fc66827,-6050354667351332811,256,0,0,#version 450 core\r\n// XRENGINE_UBER_GENERATED_VARIANT\r\n// variant-hash: 0x4faa440a0fc66827\r\n\r\n//@property(name="_Color"\, display="Color"\, mode=static)\r\nvec4 ResolveColor() { return vec4(1.0\, 1.0\, 1.0\, 1.0); }\r\nvoid main() { vec4 color = ResolveColor(); }\r\n
        """;

    private static string BuildUberVariantBaselineContract()
    {
        const string source =
            """
            #version 450 core
            //@feature(id="emission", name="Emission", default=on)
            #ifndef XRENGINE_UBER_DISABLE_EMISSION
            //@property(name="_EmissionColor", display="Emission", mode=static)
            uniform vec4 _EmissionColor;
            //@property(name="_EmissionMap", display="Emission Map", slot=texture)
            uniform sampler2D _EmissionMap;
            vec4 ResolveEmission() { return _EmissionColor; }
            #endif
            //@feature(id="matcap", name="Matcap", default=on)
            #ifndef XRENGINE_UBER_DISABLE_MATCAP
            //@property(name="_MatcapMap", display="Matcap Map", slot=texture)
            uniform sampler2D _MatcapMap;
            vec4 ResolveMatcap() { return vec4(1.0); }
            #endif
            //@feature(id="parallax", name="Parallax", default=on)
            #ifndef XRENGINE_UBER_DISABLE_PARALLAX
            //@property(name="_ParallaxAmount", display="Parallax Amount", mode=static)
            uniform float _ParallaxAmount;
            float ResolveParallax() { return _ParallaxAmount; }
            #endif
            //@property(name="_Color", display="Color", mode=static)
            uniform vec4 _Color;
            vec4 ResolveColor() { return _Color; }
            void main() { vec4 color = ResolveColor(); }
            """;

        (string Label, string[] DisabledFeatures)[] cases =
        [
            ("minimal", new[] { "emission", "matcap", "parallax" }),
            ("common", new[] { "parallax" }),
            ("maximal", Array.Empty<string>()),
        ];

        System.Text.StringBuilder contract = new();
        contract.Append("label,variant_hash,source_version,generated_source_length,animated_count,sampler_count,source\n");
        foreach ((string label, string[] disabled) in cases)
        {
            XRMaterial material = CreateUberMaterial(source,
                new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_Color"),
                new ShaderVector4(new Vector4(0.1f, 0.1f, 0.1f, 1.0f), "_EmissionColor"),
                new ShaderFloat(0.02f, "_ParallaxAmount"));

            material.EnsureUberStateInitialized();
            foreach (string featureId in disabled)
                material.SetUberFeatureEnabled(featureId, false);

            UberShaderVariantBuilder.PreparedUberVariant prepared = PrepareVariantForTests(material);
            string generatedSource = prepared.FragmentShader.Source.Text ?? string.Empty;
            contract
                .Append(label).Append(',')
                .Append("0x").Append(prepared.Request.VariantHash.ToString("x16")).Append(',')
                .Append(prepared.Request.SourceVersion).Append(',')
                .Append(prepared.GeneratedSourceLength).Append(',')
                .Append(prepared.UniformCount).Append(',')
                .Append(prepared.SamplerCount).Append(',')
                .Append(EscapeBaselineSource(generatedSource))
                .Append('\n');
        }

        return contract.ToString().TrimEnd('\n');
    }

    private static string EscapeBaselineSource(string source)
        => source
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal);

    private static string ResolveWorkspacePath(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }

    private static void WaitForActiveUberVariant(XRMaterial material)
    {
        bool completed = SpinWait.SpinUntil(() =>
        {
            EUberMaterialVariantStage stage = material.UberVariantStatus.Stage;
            return stage is EUberMaterialVariantStage.Active or EUberMaterialVariantStage.Failed;
        }, TimeSpan.FromSeconds(5));

        completed.ShouldBeTrue();
        material.UberVariantStatus.Stage.ShouldBe(EUberMaterialVariantStage.Active, customMessage: material.UberVariantStatus.FailureReason);
    }

    private static string GetFragmentSource(XRMaterial material)
    {
        XRShader? fragmentShader = material.GetShader(EShaderType.Fragment);
        fragmentShader.ShouldNotBeNull();
        fragmentShader!.Source.ShouldNotBeNull();
        fragmentShader.Source!.Text.ShouldNotBeNull();
        return fragmentShader.Source.Text!;
    }
}
