using System.Globalization;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Editor.MaterialAuthoring;

/// <summary>
/// Native adapter for Unity/Poiyomi render-state properties used by the pinned
/// `_Mode` action graph. Raw imported values are retained in editor metadata
/// while executable state is written to base/add/outline render options.
/// </summary>
public static class MaterialRenderStateActionAdapter
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "_BlendOp", "_BlendOpAlpha", "_SrcBlend", "_DstBlend",
        "_SrcBlendAlpha", "_DstBlendAlpha",
        "_AddBlendOp", "_AddBlendOpAlpha", "_AddSrcBlend", "_AddDstBlend",
        "_AddSrcBlendAlpha", "_AddDstBlendAlpha",
        "_OutlineBlendOp", "_OutlineBlendOpAlpha",
        "_OutlineSrcBlend", "_OutlineDstBlend",
        "_OutlineSrcBlendAlpha", "_OutlineDstBlendAlpha",
        "_ZWrite", "_ZTest", "_Cull", "_AlphaToCoverage", "_ColorMask",
        "_OutlineZWrite", "_OutlineZTest", "_OutlineCull",
    };

    public static bool IsSupported(string target) => Supported.Contains(target);

    public static string? Validate(string target, string? value)
    {
        if (!IsSupported(target))
            return $"Render-state property '{target}' is not supported.";
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            return $"Render-state property '{target}' requires an integer value.";
        try
        {
            ValidateValue(target, number);
            return null;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return exception.Message;
        }
    }

    public static void Apply(XRMaterial material, string target, string? value)
    {
        int number = int.Parse(value!, CultureInfo.InvariantCulture);
        ValidateValue(target, number);
        RenderingParameters options = ResolveOptions(material, target);
        string localTarget = target.StartsWith("_Add", StringComparison.Ordinal)
            ? $"_{target[4..]}"
            : target.StartsWith("_Outline", StringComparison.Ordinal)
                ? $"_{target[8..]}"
                : target;
        BlendMode blend = options.BlendModeAllDrawBuffers ??= BlendMode.EnabledOpaque();
        switch (localTarget)
        {
            case "_BlendOp":
                blend.RgbEquation = TranslateBlendOperation(number);
                break;
            case "_BlendOpAlpha":
                blend.AlphaEquation = TranslateBlendOperation(number);
                break;
            case "_SrcBlend":
                blend.RgbSrcFactor = TranslateBlendFactor(number);
                break;
            case "_DstBlend":
                blend.RgbDstFactor = TranslateBlendFactor(number);
                break;
            case "_SrcBlendAlpha":
                blend.AlphaSrcFactor = TranslateBlendFactor(number);
                break;
            case "_DstBlendAlpha":
                blend.AlphaDstFactor = TranslateBlendFactor(number);
                break;
            case "_ZWrite":
                options.DepthTest.UpdateDepth = number != 0;
                break;
            case "_ZTest":
                options.DepthTest.Enabled = number == 0
                    ? ERenderParamUsage.Disabled
                    : ERenderParamUsage.Enabled;
                if (number != 0)
                    options.DepthTest.Function = TranslateComparison(number);
                break;
            case "_Cull":
                options.CullMode = TranslateCull(number);
                break;
            case "_AlphaToCoverage":
                options.AlphaToCoverage = number == 0
                    ? ERenderParamUsage.Disabled
                    : ERenderParamUsage.Enabled;
                break;
            case "_ColorMask":
                options.WriteRed = (number & 1) != 0;
                options.WriteGreen = (number & 2) != 0;
                options.WriteBlue = (number & 4) != 0;
                options.WriteAlpha = (number & 8) != 0;
                break;
        }

        blend.Enabled =
            blend.RgbSrcFactor == EBlendingFactor.One &&
            blend.RgbDstFactor == EBlendingFactor.Zero &&
            blend.AlphaSrcFactor == EBlendingFactor.One &&
            blend.AlphaDstFactor == EBlendingFactor.Zero
                ? ERenderParamUsage.Disabled
                : ERenderParamUsage.Enabled;
        MaterialAuthoringMetadataStore.Instance.Get(material)
            .LocalOverrides[$"renderState:{target}"] = number.ToString(CultureInfo.InvariantCulture);
    }

    public static Action CaptureUndo(XRMaterial material, string target)
    {
        RenderingParameters options = ResolveOptions(material, target);
        RenderStateSnapshot snapshot = RenderStateSnapshot.Capture(options);
        MaterialAuthoringMetadata metadata = MaterialAuthoringMetadataStore.Instance.Get(material);
        string key = $"renderState:{target}";
        bool hadRaw = metadata.LocalOverrides.TryGetValue(key, out string? raw);
        return () =>
        {
            snapshot.Restore(options);
            if (hadRaw)
                metadata.LocalOverrides[key] = raw!;
            else
                metadata.LocalOverrides.Remove(key);
        };
    }

    private static RenderingParameters ResolveOptions(XRMaterial material, string target)
    {
        if (target.StartsWith("_Add", StringComparison.Ordinal))
            return material.PassSet.ForwardAddRenderOptions ?? material.RenderOptions;
        if (target.StartsWith("_Outline", StringComparison.Ordinal) &&
            material.PassSet.TryGetPass(EMaterialPassIdentity.Outline, out MaterialPassDefinition outline))
            return outline.RenderOptions;
        return material.RenderOptions;
    }

    private static void ValidateValue(string target, int value)
    {
        if (target.EndsWith("Blend", StringComparison.Ordinal) ||
            target.EndsWith("BlendAlpha", StringComparison.Ordinal))
            _ = TranslateBlendFactor(value);
        if (target.EndsWith("BlendOp", StringComparison.Ordinal) ||
            target.EndsWith("BlendOpAlpha", StringComparison.Ordinal))
            _ = TranslateBlendOperation(value);
        if (target.EndsWith("ZTest", StringComparison.Ordinal))
            _ = value == 0 ? EComparison.Always : TranslateComparison(value);
        if (target.EndsWith("Cull", StringComparison.Ordinal))
            _ = TranslateCull(value);
        if (target.EndsWith("ZWrite", StringComparison.Ordinal) ||
            target.EndsWith("AlphaToCoverage", StringComparison.Ordinal))
        {
            if (value is not (0 or 1))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Boolean render state must be 0 or 1.");
        }
        if (target.EndsWith("ColorMask", StringComparison.Ordinal) && value is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Color mask must be between 0 and 15.");
    }

    private static EBlendingFactor TranslateBlendFactor(int value)
        => value switch
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
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown Unity blend factor."),
        };

    private static EBlendEquationMode TranslateBlendOperation(int value)
        => value switch
        {
            0 => EBlendEquationMode.FuncAdd,
            1 => EBlendEquationMode.FuncSubtract,
            2 => EBlendEquationMode.FuncReverseSubtract,
            3 => EBlendEquationMode.Min,
            4 => EBlendEquationMode.Max,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown Unity blend operation."),
        };

    private static EComparison TranslateComparison(int value)
        => value switch
        {
            1 => EComparison.Never,
            2 => EComparison.Less,
            3 => EComparison.Equal,
            4 => EComparison.Lequal,
            5 => EComparison.Greater,
            6 => EComparison.Nequal,
            7 => EComparison.Gequal,
            8 => EComparison.Always,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown Unity depth comparison."),
        };

    private static ECullMode TranslateCull(int value)
        => value switch
        {
            0 => ECullMode.None,
            1 => ECullMode.Front,
            2 => ECullMode.Back,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown Unity cull mode."),
        };

    private sealed record RenderStateSnapshot(
        bool HadBlend,
        ERenderParamUsage BlendEnabled,
        EBlendEquationMode RgbEquation,
        EBlendEquationMode AlphaEquation,
        EBlendingFactor RgbSource,
        EBlendingFactor AlphaSource,
        EBlendingFactor RgbDestination,
        EBlendingFactor AlphaDestination,
        ERenderParamUsage DepthEnabled,
        bool DepthWrite,
        EComparison DepthFunction,
        ECullMode Cull,
        ERenderParamUsage AlphaToCoverage,
        bool Red,
        bool Green,
        bool Blue,
        bool Alpha)
    {
        public static RenderStateSnapshot Capture(RenderingParameters options)
        {
            BlendMode blend = options.BlendModeAllDrawBuffers ?? BlendMode.EnabledOpaque();
            return new(
                options.BlendModeAllDrawBuffers is not null,
                blend.Enabled,
                blend.RgbEquation,
                blend.AlphaEquation,
                blend.RgbSrcFactor,
                blend.AlphaSrcFactor,
                blend.RgbDstFactor,
                blend.AlphaDstFactor,
                options.DepthTest.Enabled,
                options.DepthTest.UpdateDepth,
                options.DepthTest.Function,
                options.CullMode,
                options.AlphaToCoverage,
                options.WriteRed,
                options.WriteGreen,
                options.WriteBlue,
                options.WriteAlpha);
        }

        public void Restore(RenderingParameters options)
        {
            BlendMode blend = options.BlendModeAllDrawBuffers ??= BlendMode.EnabledOpaque();
            blend.Enabled = BlendEnabled;
            blend.RgbEquation = RgbEquation;
            blend.AlphaEquation = AlphaEquation;
            blend.RgbSrcFactor = RgbSource;
            blend.AlphaSrcFactor = AlphaSource;
            blend.RgbDstFactor = RgbDestination;
            blend.AlphaDstFactor = AlphaDestination;
            options.DepthTest.Enabled = DepthEnabled;
            options.DepthTest.UpdateDepth = DepthWrite;
            options.DepthTest.Function = DepthFunction;
            options.CullMode = Cull;
            options.AlphaToCoverage = AlphaToCoverage;
            options.WriteRed = Red;
            options.WriteGreen = Green;
            options.WriteBlue = Blue;
            options.WriteAlpha = Alpha;
            if (!HadBlend)
                options.BlendModeAllDrawBuffers = null;
        }
    }
}
