using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UberMaterialVariantTests
{
    [Test]
    public void EnsureUberStateInitialized_InfersFeatureStateAndPropertyModesFromFragmentSource()
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

        material.IsUberFeatureEnabled("emission", defaultEnabled: true).ShouldBeFalse();
        material.GetUberPropertyMode("_EmissionColor", EShaderUiPropertyMode.Unspecified, isSampler: false).ShouldBe(EShaderUiPropertyMode.Static);
        material.GetUberPropertyMode("_Color", EShaderUiPropertyMode.Unspecified, isSampler: false).ShouldBe(EShaderUiPropertyMode.Animated);
    }

    [Test]
    public void RequestUberVariantRebuild_StaticPropertyAndDisabledFeatureBecomeDefines()
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
        generatedSource.ShouldContain("#define XRENGINE_UBER_DISABLE_EMISSION 1");
        generatedSource.ShouldContain("#define _Color vec4(1.0, 0.5, 0.25, 1.0)");
        generatedSource.ShouldNotContain("uniform vec4 _Color;");
        generatedSource.ShouldContain("uniform vec4 _EmissionColor;");

        material.ActiveUberVariant.StaticProperties.ShouldContain("_Color=vec4(1.0, 0.5, 0.25, 1.0)");
        material.ActiveUberVariant.EnabledFeatures.ShouldNotContain("emission");
    }

    [Test]
    public void RequestUberVariantRebuild_AnimatedPropertyStaysAsUniform()
    {
        XRMaterial material = CreateUberMaterial(
            """
            #version 450 core
            //@property(name="_Color", display="Color", mode=static)
            uniform vec4 _Color;
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
        generatedSource.ShouldContain("#define _Color vec4(0.2, 0.4, 0.6, 1.0)");
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
            UberShaderVariantTelemetry.RecordBackendSuccess(0xABCDuL, 4.0, 6.0);

            UberShaderVariantTelemetry.Snapshot snapshot = UberShaderVariantTelemetry.GetSnapshot();

            snapshot.RequestCount.ShouldBe(2);
            snapshot.SuccessCount.ShouldBe(2);
            snapshot.FailureCount.ShouldBe(1);
            snapshot.CacheHitCount.ShouldBe(1);
            snapshot.CacheHitRate.ShouldBe(0.5, 0.0001);
            snapshot.AveragePreparationMilliseconds.ShouldBe(20.0, 0.0001);
            snapshot.AverageAdoptionMilliseconds.ShouldBe(4.0, 0.0001);
            snapshot.AverageCompileMilliseconds.ShouldBe(4.0, 0.0001);
            snapshot.AverageLinkMilliseconds.ShouldBe(6.0, 0.0001);
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
        generatedSource.ShouldContain("#define XRENGINE_UBER_DISABLE_EMISSION 1");
        generatedSource.ShouldNotContain("#define XRENGINE_UBER_DISABLE_MATCAP 1");
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
            """,
            new ShaderVector4(new Vector4(0.8f, 0.7f, 0.6f, 1.0f), "_Color"));

        material.EnsureUberStateInitialized();

        material.SetUberPropertyMode("_Color", EShaderUiPropertyMode.Animated).ShouldBeTrue();
        ShaderVector4 parameter = material.Parameter<ShaderVector4>("_Color")!;
        parameter.Value = new Vector4(0.1f, 0.2f, 0.3f, 0.4f);

        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);
        material.GetShader(EShaderType.Fragment)!.Source!.Text.ShouldContain("uniform vec4 _Color;");

        material.SetUberPropertyMode("_Color", EShaderUiPropertyMode.Static).ShouldBeTrue();
        material.UberAuthoredState.GetProperty("_Color")?.StaticLiteral.ShouldBe("vec4(0.1, 0.2, 0.3, 0.4)");

        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);
        material.GetShader(EShaderType.Fragment)!.Source!.Text.ShouldContain("#define _Color vec4(0.1, 0.2, 0.3, 0.4)");

        material.SetUberPropertyMode("_Color", EShaderUiPropertyMode.Animated).ShouldBeTrue();
        material.RequestUberVariantRebuild();
        WaitForActiveUberVariant(material);

        string finalSource = material.GetShader(EShaderType.Fragment)!.Source!.Text;
        finalSource.ShouldContain("uniform vec4 _Color;");
        finalSource.ShouldNotContain("#define _Color ");
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
    public void UberVariantTelemetry_BackendSnapshotTracksCompileLinkAndFailureStages()
    {
        UberShaderVariantTelemetry.ResetForTests();

        try
        {
            const ulong variantHash = 0x1234uL;

            UberShaderVariantTelemetry.RecordBackendCompileStarted(variantHash);
            UberShaderVariantTelemetry.TryGetBackendSnapshot(variantHash, out UberShaderVariantTelemetry.BackendSnapshot compilingSnapshot).ShouldBeTrue();
            compilingSnapshot.Stage.ShouldBe(UberShaderVariantTelemetry.BackendStage.Compiling);

            UberShaderVariantTelemetry.RecordBackendLinkStarted(variantHash, 5.0);
            UberShaderVariantTelemetry.TryGetBackendSnapshot(variantHash, out UberShaderVariantTelemetry.BackendSnapshot linkingSnapshot).ShouldBeTrue();
            linkingSnapshot.Stage.ShouldBe(UberShaderVariantTelemetry.BackendStage.Linking);
            linkingSnapshot.CompileMilliseconds.ShouldBe(5.0, 0.0001);

            UberShaderVariantTelemetry.RecordBackendFailure(variantHash, "link failed", 5.0, 1.5);
            UberShaderVariantTelemetry.TryGetBackendSnapshot(variantHash, out UberShaderVariantTelemetry.BackendSnapshot failedSnapshot).ShouldBeTrue();
            failedSnapshot.Stage.ShouldBe(UberShaderVariantTelemetry.BackendStage.Failed);
            failedSnapshot.CompileMilliseconds.ShouldBe(5.0, 0.0001);
            failedSnapshot.LinkMilliseconds.ShouldBe(1.5, 0.0001);
            failedSnapshot.FailureReason.ShouldBe("link failed");
        }
        finally
        {
            UberShaderVariantTelemetry.ResetForTests();
        }
    }

    private static XRMaterial CreateUberMaterial(string fragmentSource, params ShaderVar[] parameters)
    {
        TextFile source = TextFile.FromText(fragmentSource);
        source.FilePath = "UberShader.frag";
        source.Name = "UberShader.frag";

        XRMaterial material = new()
        {
            Parameters = parameters.Length == 0 ? [] : parameters,
        };

        material.SetShader(EShaderType.Fragment, new XRShader(EShaderType.Fragment, source), coerceShaderType: true);
        return material;
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
}