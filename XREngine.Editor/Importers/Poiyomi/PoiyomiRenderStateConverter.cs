using System.Globalization;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// Converts Poiyomi Toon 9.3.64 ShaderLab pass and fixed-function state into
/// the engine's shared-authored-state material pass architecture.
/// </summary>
public static class PoiyomiRenderStateConverter
{
    private const int OpaqueQueue = 2000;
    private const int CutoutQueue = 2450;
    private const int TransClippingQueue = 2460;
    private const int TransparentQueue = 3000;
    private const EUniformRequirements UberEngineUniformRequirements =
        EUniformRequirements.Camera |
        EUniformRequirements.Lights |
        EUniformRequirements.AmbientOcclusion |
        EUniformRequirements.ViewportDimensions |
        EUniformRequirements.ClipSpacePolicy |
        EUniformRequirements.RenderTime;

    public static PoiyomiRenderStateConversion Convert(
        UnityMaterialDocument document,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(diagnostics);

        PoiyomiRenderPreset preset = ResolvePreset(document, diagnostics);
        int presetQueue = GetPresetQueue(preset);
        int sourceQueue = document.CustomRenderQueue >= 0 ? document.CustomRenderQueue : presetQueue;
        int queuePriority = sourceQueue - presetQueue;
        bool alphaToCoverage = GetBool(document, "_AlphaToCoverage", false);
        float cutoff = Math.Clamp(GetFloat(document, "_Cutoff", GetPresetCutoff(preset)), 0.0f, 1.0f);
        float polygonOffsetFactor = GetFloat(document, "_OffsetFactor", 0.0f);
        float polygonOffsetUnits = GetFloat(document, "_OffsetUnits", 0.0f);
        bool ignoreFog = GetBool(document, "_IgnoreFog", false);
        ulong coverageHash = ComputePositionOpacityStateHash(document, cutoff);

        RenderingParameters baseOptions = CreateBaseOptions(document, preset, alphaToCoverage, diagnostics);
        RenderingParameters additiveOptions = CreateAdditiveOptions(document, baseOptions, diagnostics);
        RenderingParameters outlineOptions = CreateOutlineOptions(document, alphaToCoverage, diagnostics);
        RenderingParameters coverageOptions = CloneCoverageOptions(baseOptions);
        RenderingParameters earlyDepthOptions = CloneCoverageOptions(baseOptions);
        earlyDepthOptions.WriteRed = false;
        earlyDepthOptions.WriteGreen = false;
        earlyDepthOptions.WriteBlue = false;
        earlyDepthOptions.WriteAlpha = false;
        earlyDepthOptions.BlendModeAllDrawBuffers = BlendMode.Disabled();
        earlyDepthOptions.DepthTest.UpdateDepth = true;

        bool baseEnabled = IsSourcePassEnabled(document, "Base");
        bool earlyDepthEnabled = GetBool(document, "_RenderingEarlyZEnabled", false) &&
                                 IsSourcePassEnabled(document, "EarlyZ");
        bool shadowEnabled = IsSourcePassEnabled(document, "ShadowCaster");
        bool outlineEnabled = (GetBool(document, "_EnableOutlines", false) ||
                               GetBool(document, "_OutlinesEnabled", false) ||
                               GetBool(document, "_UseOutline", false)) &&
                              IsSourcePassEnabled(document, "Outline");
        bool forwardAddEnabled = IsSourcePassEnabled(document, "Add");

        int baseRenderPass = ResolveRenderPass(preset, alphaToCoverage);
        List<MaterialPassDefinition> passes = new(9)
        {
            CreatePass(EMaterialPassIdentity.EarlyDepth, 100, (int)EDefaultRenderPass.PreRender, earlyDepthEnabled,
                "EarlyZ", earlyDepthOptions, coverageHash, ["XRENGINE_EARLY_DEPTH_PASS"], polygonOffsetFactor, polygonOffsetUnits, ignoreFog),
            CreatePass(EMaterialPassIdentity.DepthNormal, 200, (int)EDefaultRenderPass.PreRender, baseEnabled,
                null, CloneCoverageOptions(coverageOptions), coverageHash, ["XRENGINE_DEPTH_NORMAL_PREPASS"], polygonOffsetFactor, polygonOffsetUnits, ignoreFog),
            CreatePass(EMaterialPassIdentity.Shadow, 300, (int)EDefaultRenderPass.PreRender, shadowEnabled,
                "ShadowCaster", CloneCoverageOptions(coverageOptions), coverageHash, ["XRENGINE_SHADOW_CASTER_PASS"], polygonOffsetFactor, polygonOffsetUnits, ignoreFog),
            CreatePass(EMaterialPassIdentity.Velocity, 400, (int)EDefaultRenderPass.PreRender, baseEnabled,
                null, CloneCoverageOptions(coverageOptions), coverageHash, ["XRENGINE_VELOCITY_PASS"], polygonOffsetFactor, polygonOffsetUnits, ignoreFog),
            CreatePass(EMaterialPassIdentity.TransformId, 410, (int)EDefaultRenderPass.PreRender, baseEnabled,
                null, CloneCoverageOptions(coverageOptions), coverageHash, ["XRENGINE_TRANSFORM_ID_PASS"], polygonOffsetFactor, polygonOffsetUnits, ignoreFog),
            CreatePass(EMaterialPassIdentity.Picking, 420, (int)EDefaultRenderPass.PreRender, baseEnabled,
                null, CloneCoverageOptions(coverageOptions), coverageHash, ["XRENGINE_PICKING_PASS"], polygonOffsetFactor, polygonOffsetUnits, ignoreFog),
            CreatePass(EMaterialPassIdentity.Reflection, 430, baseRenderPass, baseEnabled,
                null, CloneCoverageOptions(coverageOptions), coverageHash, ["XRENGINE_REFLECTION_PASS"], polygonOffsetFactor, polygonOffsetUnits, ignoreFog),
            CreatePass(EMaterialPassIdentity.Base, 500, baseRenderPass, baseEnabled,
                "Base", baseOptions, coverageHash, [], polygonOffsetFactor, polygonOffsetUnits, ignoreFog),
            CreateOutlinePass(outlineEnabled, baseRenderPass, outlineOptions, coverageHash, ignoreFog),
        };

        if (!forwardAddEnabled)
        {
            diagnostics.Add(new MaterialConversionDiagnostic(
                MaterialConversionDiagnosticCodes.IntentionalNativeDifference,
                MaterialConversionDiagnosticSeverity.Info,
                "Poiyomi's disabled Add pass is preserved, so no separate additive compatibility pass is requested; ordinary Forward+ base-pass lighting remains active.",
                "Add"));
        }
        else
        {
            diagnostics.Add(new MaterialConversionDiagnostic(
                MaterialConversionDiagnosticCodes.IntentionalNativeDifference,
                MaterialConversionDiagnosticSeverity.Info,
                "Poiyomi ForwardAdd lighting is folded into the single Forward+ base pass; its independently authored additive blend state is retained in the conversion report for diagnostics.",
                "Add"));
        }

        return new PoiyomiRenderStateConversion
        {
            Preset = preset,
            PrimaryPassIdentity = EMaterialPassIdentity.Base,
            TransparencyMode = ResolveTransparencyMode(preset, alphaToCoverage),
            PassSet = new MaterialPassSet
            {
                Passes = [.. passes],
                DisabledSourcePasses = [.. document.DisabledShaderPasses.OrderBy(static name => name, StringComparer.Ordinal)],
                SourceRenderQueue = sourceQueue,
                QueuePriority = queuePriority,
                ForwardAddRenderOptions = additiveOptions,
                ForwardAddPolicy = forwardAddEnabled
                    ? EMaterialForwardAddPolicy.FoldedIntoForwardPlusBase
                    : EMaterialForwardAddPolicy.Disabled,
            },
        };
    }

    private static MaterialPassDefinition CreatePass(
        EMaterialPassIdentity identity,
        int order,
        int renderPass,
        bool enabled,
        string? sourcePassName,
        RenderingParameters options,
        ulong coverageHash,
        string[] macros,
        float polygonOffsetFactor,
        float polygonOffsetUnits,
        bool ignoreFog)
        => new()
        {
            Identity = identity,
            Order = order,
            RenderPass = renderPass,
            Enabled = enabled,
            SourcePassName = sourcePassName,
            VariantMacros = macros,
            RenderOptions = options,
            CoverageRules = EMaterialPassCoverageRules.All,
            PolygonOffsetFactor = polygonOffsetFactor,
            PolygonOffsetUnits = polygonOffsetUnits,
            IgnoreFog = ignoreFog,
            PositionOpacityStateHash = coverageHash,
        };

    private static MaterialPassDefinition CreateOutlinePass(
        bool enabled,
        int renderPass,
        RenderingParameters options,
        ulong coverageHash,
        bool ignoreFog)
        => new()
        {
            Identity = EMaterialPassIdentity.Outline,
            Order = 600,
            RenderPass = renderPass,
            Enabled = enabled,
            SourcePassName = "Outline",
            VertexShaderPath = Path.Combine("Uber", "UberShader.vert"),
            FragmentShaderPath = Path.Combine("Uber", "UberShader.frag"),
            VariantMacros = ["XRENGINE_OUTLINE_PASS"],
            RenderOptions = options,
            CoverageRules = EMaterialPassCoverageRules.All,
            IgnoreFog = ignoreFog,
            PositionOpacityStateHash = coverageHash,
        };

    private static RenderingParameters CreateBaseOptions(
        UnityMaterialDocument document,
        PoiyomiRenderPreset preset,
        bool alphaToCoverage,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        (int srcRgb, int dstRgb, int srcAlpha, int dstAlpha) = GetPresetBlend(preset);
        BlendMode blend = new()
        {
            Enabled = RequiresBlending(preset) ? ERenderParamUsage.Enabled : ERenderParamUsage.Disabled,
            RgbSrcFactor = MapBlendFactor(document, "_SrcBlend", srcRgb, diagnostics),
            RgbDstFactor = MapBlendFactor(document, "_DstBlend", dstRgb, diagnostics),
            AlphaSrcFactor = MapBlendFactor(document, "_SrcBlendAlpha", srcAlpha, diagnostics),
            AlphaDstFactor = MapBlendFactor(document, "_DstBlendAlpha", dstAlpha, diagnostics),
            RgbEquation = MapBlendOperation(document, "_BlendOp", 0, diagnostics),
            AlphaEquation = MapBlendOperation(document, "_BlendOpAlpha", 4, diagnostics),
        };

        return new RenderingParameters
        {
            CullMode = MapCull(document, "_Cull", ECullMode.Back, diagnostics),
            DepthTest = CreateDepthTest(
                document,
                "_ZTest",
                "_ZWrite",
                PresetWritesDepth(preset),
                diagnostics),
            StencilTest = CreateStencil(document, "_Stencil", diagnostics),
            BlendModeAllDrawBuffers = blend,
            AlphaToCoverage = alphaToCoverage ? ERenderParamUsage.Enabled : ERenderParamUsage.Disabled,
            WriteRed = (GetInt(document, "_ColorMask", 15) & 1) != 0,
            WriteGreen = (GetInt(document, "_ColorMask", 15) & 2) != 0,
            WriteBlue = (GetInt(document, "_ColorMask", 15) & 4) != 0,
            WriteAlpha = (GetInt(document, "_ColorMask", 15) & 8) != 0,
            RequiredEngineUniforms = UberEngineUniformRequirements,
        };
    }

    private static RenderingParameters CreateAdditiveOptions(
        UnityMaterialDocument document,
        RenderingParameters baseOptions,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        RenderingParameters options = CloneCoverageOptions(baseOptions);
        options.DepthTest.UpdateDepth = false;
        options.BlendModeAllDrawBuffers = new BlendMode
        {
            Enabled = ERenderParamUsage.Enabled,
            RgbSrcFactor = MapBlendFactor(document, "_AddSrcBlend", 1, diagnostics),
            RgbDstFactor = MapBlendFactor(document, "_AddDstBlend", 1, diagnostics),
            AlphaSrcFactor = MapBlendFactor(document, "_AddSrcBlendAlpha", 0, diagnostics),
            AlphaDstFactor = MapBlendFactor(document, "_AddDstBlendAlpha", 1, diagnostics),
            RgbEquation = MapBlendOperation(document, "_AddBlendOp", 4, diagnostics),
            AlphaEquation = MapBlendOperation(document, "_AddBlendOpAlpha", 4, diagnostics),
        };
        return options;
    }

    private static RenderingParameters CreateOutlineOptions(
        UnityMaterialDocument document,
        bool alphaToCoverage,
        ICollection<MaterialConversionDiagnostic> diagnostics)
        => new()
        {
            CullMode = MapCull(document, "_OutlineCull", ECullMode.Front, diagnostics),
            DepthTest = CreateDepthTest(
                document,
                "_OutlineZTest",
                "_OutlineZWrite",
                true,
                diagnostics),
            StencilTest = CreateStencil(document, "_OutlineStencil", diagnostics),
            BlendModeAllDrawBuffers = new BlendMode
            {
                Enabled = ERenderParamUsage.Enabled,
                RgbSrcFactor = MapBlendFactor(document, "_OutlineSrcBlend", 1, diagnostics),
                RgbDstFactor = MapBlendFactor(document, "_OutlineDstBlend", 0, diagnostics),
                AlphaSrcFactor = MapBlendFactor(document, "_OutlineSrcBlendAlpha", 1, diagnostics),
                AlphaDstFactor = MapBlendFactor(document, "_OutlineDstBlendAlpha", 0, diagnostics),
                RgbEquation = MapBlendOperation(document, "_OutlineBlendOp", 0, diagnostics),
                AlphaEquation = MapBlendOperation(document, "_OutlineBlendOpAlpha", 4, diagnostics),
            },
            AlphaToCoverage = alphaToCoverage ? ERenderParamUsage.Enabled : ERenderParamUsage.Disabled,
            RequiredEngineUniforms = UberEngineUniformRequirements,
        };

    private static RenderingParameters CloneCoverageOptions(RenderingParameters source)
        => new()
        {
            CullMode = source.CullMode,
            Winding = source.Winding,
            DepthTest = new DepthTest
            {
                Enabled = source.DepthTest.Enabled,
                UpdateDepth = source.DepthTest.UpdateDepth,
                Function = source.DepthTest.Function,
            },
            StencilTest = CloneStencil(source.StencilTest),
            BlendModeAllDrawBuffers = source.BlendModeAllDrawBuffers,
            AlphaToCoverage = source.AlphaToCoverage,
            WriteRed = source.WriteRed,
            WriteGreen = source.WriteGreen,
            WriteBlue = source.WriteBlue,
            WriteAlpha = source.WriteAlpha,
            RequiredEngineUniforms = source.RequiredEngineUniforms,
            ExcludeFromGpuIndirect = source.ExcludeFromGpuIndirect,
            ExcludeFromCpuOcclusion = source.ExcludeFromCpuOcclusion,
            TextureArrayPolicy = source.TextureArrayPolicy,
        };

    private static StencilTest CloneStencil(StencilTest source)
        => new()
        {
            Enabled = source.Enabled,
            FrontFace = CloneStencilFace(source.FrontFace),
            BackFace = CloneStencilFace(source.BackFace),
        };

    private static StencilTestFace CloneStencilFace(StencilTestFace source)
        => new()
        {
            Reference = source.Reference,
            ReadMask = source.ReadMask,
            WriteMask = source.WriteMask,
            Function = source.Function,
            BothFailOp = source.BothFailOp,
            StencilPassDepthFailOp = source.StencilPassDepthFailOp,
            BothPassOp = source.BothPassOp,
        };

    private static StencilTest CreateStencil(
        UnityMaterialDocument document,
        string prefix,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        int reference = GetInt(document, prefix + "Ref", 0);
        uint readMask = unchecked((uint)GetInt(document, prefix + "ReadMask", 255));
        uint writeMask = unchecked((uint)GetInt(document, prefix + "WriteMask", 255));
        EComparison commonComparison = MapComparison(document, prefix + "CompareFunction", 8, diagnostics);
        EStencilOp commonPass = MapStencilOperation(document, prefix + "PassOp", 0, diagnostics);
        EStencilOp commonFail = MapStencilOperation(document, prefix + "FailOp", 0, diagnostics);
        EStencilOp commonDepthFail = MapStencilOperation(document, prefix + "ZFailOp", 0, diagnostics);
        bool authored = document.GetPropertyNames().Any(name => name.StartsWith(prefix, StringComparison.Ordinal));

        return new StencilTest
        {
            Enabled = authored ? ERenderParamUsage.Enabled : ERenderParamUsage.Disabled,
            FrontFace = CreateStencilFace(document, prefix + "Front", reference, readMask, writeMask,
                commonComparison, commonPass, commonFail, commonDepthFail, diagnostics),
            BackFace = CreateStencilFace(document, prefix + "Back", reference, readMask, writeMask,
                commonComparison, commonPass, commonFail, commonDepthFail, diagnostics),
        };
    }

    private static StencilTestFace CreateStencilFace(
        UnityMaterialDocument document,
        string prefix,
        int reference,
        uint readMask,
        uint writeMask,
        EComparison commonComparison,
        EStencilOp commonPass,
        EStencilOp commonFail,
        EStencilOp commonDepthFail,
        ICollection<MaterialConversionDiagnostic> diagnostics)
        => new()
        {
            Reference = reference,
            ReadMask = readMask,
            WriteMask = writeMask,
            Function = MapComparison(document, prefix + "CompareFunction", (int)commonComparison + 1, diagnostics),
            BothPassOp = MapStencilOperation(document, prefix + "PassOp", ToUnityStencilOperation(commonPass), diagnostics),
            BothFailOp = MapStencilOperation(document, prefix + "FailOp", ToUnityStencilOperation(commonFail), diagnostics),
            StencilPassDepthFailOp = MapStencilOperation(document, prefix + "ZFailOp", ToUnityStencilOperation(commonDepthFail), diagnostics),
        };

    private static PoiyomiRenderPreset ResolvePreset(
        UnityMaterialDocument document,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        int fallback = document.CustomRenderQueue >= TransparentQueue ? 3 : 0;
        int raw = GetInt(document, "_Mode", fallback);
        if (Enum.IsDefined((PoiyomiRenderPreset)raw))
            return (PoiyomiRenderPreset)raw;

        ReportOutOfRange("_Mode", raw, PoiyomiRenderPreset.Opaque, diagnostics);
        return PoiyomiRenderPreset.Opaque;
    }

    private static ECullMode MapCull(
        UnityMaterialDocument document,
        string property,
        ECullMode fallback,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        int raw = GetInt(document, property, fallback switch
        {
            ECullMode.None => 0,
            ECullMode.Front => 1,
            _ => 2,
        });
        return raw switch
        {
            0 => ECullMode.None,
            1 => ECullMode.Front,
            2 => ECullMode.Back,
            _ => ReportOutOfRange(property, raw, fallback, diagnostics),
        };
    }

    private static DepthTest CreateDepthTest(
        UnityMaterialDocument document,
        string testProperty,
        string writeProperty,
        bool defaultWrite,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        int rawComparison = GetInt(document, testProperty, 4);
        return new DepthTest
        {
            Enabled = rawComparison == 0 ? ERenderParamUsage.Disabled : ERenderParamUsage.Enabled,
            UpdateDepth = GetBool(document, writeProperty, defaultWrite),
            Function = MapComparison(document, testProperty, 4, diagnostics),
        };
    }

    private static EComparison MapComparison(
        UnityMaterialDocument document,
        string property,
        int fallback,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        int raw = GetInt(document, property, fallback);
        return raw switch
        {
            0 or 8 => EComparison.Always,
            1 => EComparison.Never,
            2 => EComparison.Less,
            3 => EComparison.Equal,
            4 => EComparison.Lequal,
            5 => EComparison.Greater,
            6 => EComparison.Nequal,
            7 => EComparison.Gequal,
            _ => ReportOutOfRange(property, raw, EComparison.Lequal, diagnostics),
        };
    }

    private static EBlendingFactor MapBlendFactor(
        UnityMaterialDocument document,
        string property,
        int fallback,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        int raw = GetInt(document, property, fallback);
        return raw switch
        {
            0 => EBlendingFactor.Zero,
            1 => EBlendingFactor.One,
            2 => EBlendingFactor.DstColor,
            3 => EBlendingFactor.SrcColor,
            4 => EBlendingFactor.OneMinusDstColor,
            5 => EBlendingFactor.SrcAlpha,
            6 => EBlendingFactor.OneMinusSrcColor,
            7 => EBlendingFactor.DstAlpha,
            8 => EBlendingFactor.OneMinusDstAlpha,
            9 => EBlendingFactor.SrcAlphaSaturate,
            10 => EBlendingFactor.OneMinusSrcAlpha,
            _ => ReportOutOfRange(property, raw, EBlendingFactor.One, diagnostics),
        };
    }

    private static EBlendEquationMode MapBlendOperation(
        UnityMaterialDocument document,
        string property,
        int fallback,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        int raw = GetInt(document, property, fallback);
        return raw switch
        {
            0 => EBlendEquationMode.FuncAdd,
            1 => EBlendEquationMode.FuncSubtract,
            2 => EBlendEquationMode.FuncReverseSubtract,
            3 => EBlendEquationMode.Min,
            4 => EBlendEquationMode.Max,
            _ => ReportOutOfRange(property, raw, EBlendEquationMode.FuncAdd, diagnostics),
        };
    }

    private static EStencilOp MapStencilOperation(
        UnityMaterialDocument document,
        string property,
        int fallback,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        int raw = GetInt(document, property, fallback);
        return raw switch
        {
            0 => EStencilOp.Keep,
            1 => EStencilOp.Zero,
            2 => EStencilOp.Replace,
            3 => EStencilOp.Incr,
            4 => EStencilOp.Decr,
            5 => EStencilOp.Invert,
            6 => EStencilOp.IncrWrap,
            7 => EStencilOp.DecrWrap,
            _ => ReportOutOfRange(property, raw, EStencilOp.Keep, diagnostics),
        };
    }

    private static int ToUnityStencilOperation(EStencilOp operation)
        => operation switch
        {
            EStencilOp.Keep => 0,
            EStencilOp.Zero => 1,
            EStencilOp.Replace => 2,
            EStencilOp.Incr => 3,
            EStencilOp.Decr => 4,
            EStencilOp.Invert => 5,
            EStencilOp.IncrWrap => 6,
            EStencilOp.DecrWrap => 7,
            _ => 0,
        };

    private static T ReportOutOfRange<T>(
        string property,
        int raw,
        T fallback,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        diagnostics.Add(new MaterialConversionDiagnostic(
            MaterialConversionDiagnosticCodes.EnumValueOutOfRange,
            MaterialConversionDiagnosticSeverity.Warning,
            $"Serialized enum value {raw.ToString(CultureInfo.InvariantCulture)} is not supported by the pinned Poiyomi 9.3.64 mapping; using '{fallback}'.",
            property));
        return fallback;
    }

    private static ETransparencyMode ResolveTransparencyMode(PoiyomiRenderPreset preset, bool alphaToCoverage)
    {
        if (alphaToCoverage)
            return ETransparencyMode.AlphaToCoverage;

        return preset switch
        {
            PoiyomiRenderPreset.Opaque => ETransparencyMode.Opaque,
            PoiyomiRenderPreset.Cutout or PoiyomiRenderPreset.TransClipping => ETransparencyMode.Masked,
            PoiyomiRenderPreset.Transparent => ETransparencyMode.PremultipliedAlpha,
            PoiyomiRenderPreset.Additive => ETransparencyMode.Additive,
            _ => ETransparencyMode.AlphaBlend,
        };
    }

    private static int ResolveRenderPass(PoiyomiRenderPreset preset, bool alphaToCoverage)
    {
        if (alphaToCoverage || preset is PoiyomiRenderPreset.Cutout or PoiyomiRenderPreset.TransClipping)
            return (int)EDefaultRenderPass.MaskedForward;
        if (preset == PoiyomiRenderPreset.Opaque)
            return (int)EDefaultRenderPass.OpaqueForward;
        return (int)EDefaultRenderPass.TransparentForward;
    }

    private static bool RequiresBlending(PoiyomiRenderPreset preset)
        => preset is not PoiyomiRenderPreset.Opaque and not PoiyomiRenderPreset.Cutout;

    private static bool PresetWritesDepth(PoiyomiRenderPreset preset)
        => preset is PoiyomiRenderPreset.Opaque or PoiyomiRenderPreset.Cutout or PoiyomiRenderPreset.TransClipping;

    private static int GetPresetQueue(PoiyomiRenderPreset preset)
        => preset switch
        {
            PoiyomiRenderPreset.Opaque => OpaqueQueue,
            PoiyomiRenderPreset.Cutout => CutoutQueue,
            PoiyomiRenderPreset.TransClipping => TransClippingQueue,
            _ => TransparentQueue,
        };

    private static float GetPresetCutoff(PoiyomiRenderPreset preset)
        => preset switch
        {
            PoiyomiRenderPreset.Cutout => 0.5f,
            PoiyomiRenderPreset.TransClipping => 0.01f,
            PoiyomiRenderPreset.Fade => 0.002f,
            _ => 0.0f,
        };

    private static (int SrcRgb, int DstRgb, int SrcAlpha, int DstAlpha) GetPresetBlend(PoiyomiRenderPreset preset)
        => preset switch
        {
            PoiyomiRenderPreset.TransClipping or PoiyomiRenderPreset.Fade => (5, 10, 1, 1),
            PoiyomiRenderPreset.Transparent => (1, 10, 1, 1),
            PoiyomiRenderPreset.Additive => (1, 1, 1, 1),
            PoiyomiRenderPreset.SoftAdditive => (4, 1, 1, 1),
            PoiyomiRenderPreset.Multiplicative => (2, 0, 1, 1),
            PoiyomiRenderPreset.Multiplicative2X => (2, 3, 1, 1),
            _ => (1, 0, 1, 1),
        };

    private static bool IsSourcePassEnabled(UnityMaterialDocument document, string passName)
        => !document.DisabledShaderPasses.Contains(passName);

    private static bool GetBool(UnityMaterialDocument document, string name, bool fallback)
        => document.TryGetFloat(name, out float value) ? value > 0.5f : fallback;

    private static int GetInt(UnityMaterialDocument document, string name, int fallback)
        => document.TryGetInt(name, out int value) ? value : fallback;

    private static float GetFloat(UnityMaterialDocument document, string name, float fallback)
        => document.TryGetFloat(name, out float value) ? value : fallback;

    private static ulong ComputePositionOpacityStateHash(UnityMaterialDocument document, float cutoff)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;

        Add(ref hash, BitConverter.SingleToUInt32Bits(cutoff), prime);
        foreach (string name in document.GetPropertyNames().Where(IsPositionOrOpacityProperty).OrderBy(static x => x, StringComparer.Ordinal))
        {
            foreach (char character in name)
                Add(ref hash, character, prime);
            if (document.TryGetFloat(name, out float value))
                Add(ref hash, BitConverter.SingleToUInt32Bits(value), prime);
            else if (document.TryGetVector(name, out System.Numerics.Vector4 vector))
            {
                Add(ref hash, BitConverter.SingleToUInt32Bits(vector.X), prime);
                Add(ref hash, BitConverter.SingleToUInt32Bits(vector.Y), prime);
                Add(ref hash, BitConverter.SingleToUInt32Bits(vector.Z), prime);
                Add(ref hash, BitConverter.SingleToUInt32Bits(vector.W), prime);
            }
        }

        return hash;
    }

    private static bool IsPositionOrOpacityProperty(string name)
        => name.Contains("Alpha", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Dissolve", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Discard", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Vertex", StringComparison.OrdinalIgnoreCase) ||
           name is "_Cull" or "_Cutoff" or "_Mode";

    private static void Add(ref ulong hash, uint value, ulong prime)
    {
        hash ^= value;
        hash *= prime;
    }
}
