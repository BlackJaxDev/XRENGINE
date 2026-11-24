using System.Globalization;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.ShaderTranslation.ShaderLab;
using XREngine.ShaderTranslation.Translation;

namespace XREngine.Scene.Importers;

/// <summary>
/// Maps the ShaderLab render state of a translated pass to engine material state.
/// </summary>
/// <remarks>
/// The mapper reads <c>Cull</c>, <c>ZWrite</c>, <c>ZTest</c>, <c>Blend</c>, <c>BlendOp</c>, <c>ColorMask</c>,
/// and <c>AlphaToMask</c>. An argument such as <c>[_Cull]</c> takes the material value, then the
/// property default. Numeric values use the Unity enums: <c>CullMode</c>, <c>CompareFunction</c>,
/// <c>BlendMode</c>, <c>BlendOp</c>, and <c>ColorWriteMask</c>. The render queue selects the engine pass:
/// below 2450 is opaque, 2450 to 2500 is masked, and above 2500 is transparent.
/// <c>AlphaToMask On</c> selects alpha to coverage; the importer also adds a fragment discard below half alpha
/// (see <see cref="IsAlphaToMaskEnabled"/>). The legacy <c>AlphaTest</c> command has no effect on Unity programmable
/// passes, so the mapper ignores it. <c>Stencil</c> blocks and per-target blend state are not mapped yet; they cause a warning.
/// </remarks>
public static class ShaderLabRenderStateMapper
{
    private const int GeometryQueue = 2000;
    private const int AlphaTestQueue = 2450;
    private const int LastMaskedQueue = 2500;

    /// <summary>Applies the pass render state, render pass, transparency mode, and alpha cutoff to a material.</summary>
    /// <param name="material">The material to change.</param>
    /// <param name="pass">The translated pass.</param>
    /// <param name="shader">The parsed shader, for property defaults.</param>
    /// <param name="document">The Unity material, for property values and the custom queue.</param>
    /// <param name="warnings">Receives the state that could not be mapped.</param>
    public static void Apply(
        XRMaterial material,
        TranslatedPass pass,
        ShaderLabDocument shader,
        SerializedMaterialDocument document,
        List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(warnings);

        StateReader reader = new(shader, document, warnings);
        ShaderLabCommand? cull = FindLast(pass.Commands, "Cull");
        ShaderLabCommand? zWrite = FindLast(pass.Commands, "ZWrite");
        ShaderLabCommand? zTest = FindLast(pass.Commands, "ZTest");
        ShaderLabCommand? blend = FindLast(pass.Commands, "Blend");
        ShaderLabCommand? blendOp = FindLast(pass.Commands, "BlendOp");
        ShaderLabCommand? colorMask = FindLast(pass.Commands, "ColorMask");
        ShaderLabCommand? alphaToMask = FindLast(pass.Commands, "AlphaToMask");
        if (FindLast(pass.Commands, "Stencil") is not null)
            warnings.Add($"Shader '{shader.Name}' pass {pass.Label}: the Stencil block is not mapped yet.");

        BlendMode blendMode = reader.ReadBlend(blend, blendOp);
        bool alphaToCoverage = alphaToMask is not null && reader.ReadToggle(alphaToMask, false);
        int colorWriteMask = colorMask is null ? 15 : reader.ReadColorMask(colorMask);
        int comparison = zTest is null ? 4 : reader.ReadComparison(zTest);

        material.RenderOptions = new RenderingParameters
        {
            CullMode = cull is null ? ECullMode.Back : reader.ReadCull(cull),
            DepthTest = new DepthTest
            {
                // CompareFunction.Disabled turns the depth test off.
                Enabled = comparison == 0 ? ERenderParamUsage.Disabled : ERenderParamUsage.Enabled,
                UpdateDepth = zWrite is null || reader.ReadToggle(zWrite, true),
                Function = MapComparison(comparison),
            },
            BlendModeAllDrawBuffers = blendMode,
            AlphaToCoverage = alphaToCoverage ? ERenderParamUsage.Enabled : ERenderParamUsage.Disabled,
            // Unity ColorWriteMask: Alpha = 1, Blue = 2, Green = 4, Red = 8.
            WriteRed = (colorWriteMask & 8) != 0,
            WriteGreen = (colorWriteMask & 4) != 0,
            WriteBlue = (colorWriteMask & 2) != 0,
            WriteAlpha = (colorWriteMask & 1) != 0,
            RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.Lights |
                EUniformRequirements.RenderTime | EUniformRequirements.ViewportDimensions,
        };

        int queue = ResolveQueue(pass, document);
        bool blended = blendMode.Enabled == ERenderParamUsage.Enabled;
        if (alphaToCoverage)
        {
            material.TransparencyMode = ETransparencyMode.AlphaToCoverage;
            material.RenderPass = (int)EDefaultRenderPass.MaskedForward;
        }
        else if (blended || queue > LastMaskedQueue)
        {
            material.TransparencyMode = blended ? ResolveBlendedMode(blendMode) : ETransparencyMode.AlphaBlend;
            material.RenderPass = (int)EDefaultRenderPass.TransparentForward;
        }
        else if (queue >= AlphaTestQueue)
        {
            material.TransparencyMode = ETransparencyMode.Masked;
            material.RenderPass = (int)EDefaultRenderPass.MaskedForward;
        }
        else
        {
            material.TransparencyMode = ETransparencyMode.Opaque;
            material.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
        }

        if (reader.TryReadProperty("_Cutoff", out float cutoff))
            material.AlphaCutoff = Math.Clamp(cutoff, 0.0f, 1.0f);
    }

    /// <summary>
    /// Returns true when the pass sets <c>AlphaToMask On</c>. A property reference takes the material value, then the property default.
    /// </summary>
    /// <remarks>
    /// The translated fragment stage of such a pass needs a discard below half alpha, because alpha to coverage
    /// does nothing on a target with one sample. <see cref="Apply"/> reports an argument that is not valid, so this
    /// method adds no warnings.
    /// </remarks>
    public static bool IsAlphaToMaskEnabled(TranslatedPass pass, ShaderLabDocument shader, SerializedMaterialDocument document)
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentNullException.ThrowIfNull(document);

        ShaderLabCommand? alphaToMask = FindLast(pass.Commands, "AlphaToMask");
        return alphaToMask is not null && new StateReader(shader, document, []).ReadToggle(alphaToMask, false);
    }

    /// <summary>
    /// Returns the render queue: the material custom queue when it is set, otherwise the <c>Queue</c> tag.
    /// </summary>
    public static int ResolveQueue(TranslatedPass pass, SerializedMaterialDocument document)
    {
        if (document.CustomRenderQueue >= 0)
            return document.CustomRenderQueue;

        return pass.Tags.TryGetValue("Queue", out string? tag) ? ParseQueueTag(tag) : GeometryQueue;
    }

    /// <summary>Parses a queue tag such as <c>Transparent+1</c>, <c>AlphaTest</c>, or <c>2450</c>.</summary>
    public static int ParseQueueTag(string tag)
    {
        ReadOnlySpan<char> text = tag.AsSpan().Trim();
        int offset = 0;
        int sign = text.IndexOfAny('+', '-');
        if (sign > 0)
        {
            if (int.TryParse(text[(sign + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
                offset = text[sign] == '+' ? amount : -amount;
            text = text[..sign].Trim();
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            return numeric + offset;

        int baseQueue = text switch
        {
            _ when text.Equals("Background", StringComparison.OrdinalIgnoreCase) => 1000,
            _ when text.Equals("AlphaTest", StringComparison.OrdinalIgnoreCase) => AlphaTestQueue,
            _ when text.Equals("Transparent", StringComparison.OrdinalIgnoreCase) => 3000,
            _ when text.Equals("Overlay", StringComparison.OrdinalIgnoreCase) => 4000,
            _ => GeometryQueue,
        };
        return baseQueue + offset;
    }

    private static ShaderLabCommand? FindLast(IReadOnlyList<ShaderLabCommand> commands, string name)
    {
        for (int index = commands.Count - 1; index >= 0; index--)
            if (string.Equals(commands[index].Name, name, StringComparison.OrdinalIgnoreCase))
                return commands[index];

        return null;
    }

    private static ETransparencyMode ResolveBlendedMode(BlendMode blend)
        => (blend.RgbSrcFactor, blend.RgbDstFactor) switch
        {
            (EBlendingFactor.One, EBlendingFactor.OneMinusSrcAlpha) => ETransparencyMode.PremultipliedAlpha,
            (EBlendingFactor.One, EBlendingFactor.One) or (EBlendingFactor.SrcAlpha, EBlendingFactor.One) => ETransparencyMode.Additive,
            _ => ETransparencyMode.AlphaBlend,
        };

    private static EComparison MapComparison(int value)
        => value switch
        {
            1 => EComparison.Never,
            2 => EComparison.Less,
            3 => EComparison.Equal,
            5 => EComparison.Greater,
            6 => EComparison.Nequal,
            7 => EComparison.Gequal,
            0 or 8 => EComparison.Always,
            _ => EComparison.Lequal,
        };

    /// <summary>Reads command arguments as literal names or as material property references.</summary>
    private readonly struct StateReader(ShaderLabDocument shader, SerializedMaterialDocument document, List<string> warnings)
    {
        public ECullMode ReadCull(ShaderLabCommand command)
        {
            string argument = FirstArgument(command);
            if (TryReadNumber(argument, out float number))
                return (int)MathF.Round(number) switch
                {
                    0 => ECullMode.None,
                    1 => ECullMode.Front,
                    _ => ECullMode.Back,
                };

            if (argument.Equals("Off", StringComparison.OrdinalIgnoreCase))
                return ECullMode.None;
            if (argument.Equals("Front", StringComparison.OrdinalIgnoreCase))
                return ECullMode.Front;
            if (!argument.Equals("Back", StringComparison.OrdinalIgnoreCase))
                Warn(command, argument);
            return ECullMode.Back;
        }

        public bool ReadToggle(ShaderLabCommand command, bool fallback)
        {
            string argument = FirstArgument(command);
            if (TryReadNumber(argument, out float number))
                return number > 0.5f;
            if (argument.Equals("On", StringComparison.OrdinalIgnoreCase) || argument.Equals("True", StringComparison.OrdinalIgnoreCase))
                return true;
            if (argument.Equals("Off", StringComparison.OrdinalIgnoreCase) || argument.Equals("False", StringComparison.OrdinalIgnoreCase))
                return false;

            Warn(command, argument);
            return fallback;
        }

        /// <summary>Returns a Unity <c>CompareFunction</c> value.</summary>
        public int ReadComparison(ShaderLabCommand command)
        {
            string argument = FirstArgument(command);
            if (TryReadNumber(argument, out float number))
                return (int)MathF.Round(number);

            switch (argument.ToLowerInvariant())
            {
                case "never":
                    return 1;
                case "less":
                    return 2;
                case "equal":
                    return 3;
                case "lequal":
                case "lessequal":
                    return 4;
                case "greater":
                    return 5;
                case "notequal":
                    return 6;
                case "gequal":
                case "greaterequal":
                    return 7;
                case "always":
                    return 8;
                case "off":
                case "disabled":
                    return 0;
                default:
                    Warn(command, argument);
                    return 4;
            }
        }

        /// <summary>Returns a Unity <c>ColorWriteMask</c> value.</summary>
        public int ReadColorMask(ShaderLabCommand command)
        {
            string argument = FirstArgument(command);
            if (TryReadNumber(argument, out float number))
                return (int)MathF.Round(number) & 15;

            int mask = 0;
            foreach (char channel in argument)
            {
                switch (char.ToUpperInvariant(channel))
                {
                    case 'R':
                        mask |= 8;
                        break;
                    case 'G':
                        mask |= 4;
                        break;
                    case 'B':
                        mask |= 2;
                        break;
                    case 'A':
                        mask |= 1;
                        break;
                    default:
                        Warn(command, argument);
                        return 15;
                }
            }

            return mask;
        }

        public BlendMode ReadBlend(ShaderLabCommand? blend, ShaderLabCommand? blendOp)
        {
            BlendMode mode = new()
            {
                Enabled = ERenderParamUsage.Disabled,
                RgbSrcFactor = EBlendingFactor.One,
                RgbDstFactor = EBlendingFactor.Zero,
                AlphaSrcFactor = EBlendingFactor.One,
                AlphaDstFactor = EBlendingFactor.Zero,
                RgbEquation = EBlendEquationMode.FuncAdd,
                AlphaEquation = EBlendEquationMode.FuncAdd,
            };

            if (blendOp is not null)
            {
                List<string> operations = SplitArguments(blendOp);
                if (operations.Count > 0)
                {
                    mode.RgbEquation = ReadBlendOperation(blendOp, operations[0]);
                    mode.AlphaEquation = operations.Count > 1 ? ReadBlendOperation(blendOp, operations[1]) : mode.RgbEquation;
                }
            }

            if (blend is null)
                return mode;

            List<string> factors = SplitArguments(blend);
            // "Blend 1 SrcAlpha OneMinusSrcAlpha" sets one render target. The engine state applies to all targets.
            if (factors.Count is 3 or 5 && int.TryParse(factors[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                warnings.Add($"Shader '{shader.Name}' line {blend.Line}: per-target Blend state applies to all render targets.");
                factors.RemoveAt(0);
            }

            if (factors.Count == 1 && factors[0].Equals("Off", StringComparison.OrdinalIgnoreCase))
                return mode;
            if (factors.Count is not (2 or 4))
            {
                Warn(blend, blend.ArgumentText);
                return mode;
            }

            mode.RgbSrcFactor = ReadBlendFactor(blend, factors[0]);
            mode.RgbDstFactor = ReadBlendFactor(blend, factors[1]);
            mode.AlphaSrcFactor = factors.Count == 4 ? ReadBlendFactor(blend, factors[2]) : mode.RgbSrcFactor;
            mode.AlphaDstFactor = factors.Count == 4 ? ReadBlendFactor(blend, factors[3]) : mode.RgbDstFactor;
            // Blend One Zero is the same as no blending.
            bool replaces = mode.RgbSrcFactor == EBlendingFactor.One && mode.RgbDstFactor == EBlendingFactor.Zero &&
                mode.AlphaSrcFactor == EBlendingFactor.One && mode.AlphaDstFactor == EBlendingFactor.Zero;
            mode.Enabled = replaces ? ERenderParamUsage.Disabled : ERenderParamUsage.Enabled;
            return mode;
        }

        public bool TryReadProperty(string name, out float value)
        {
            if (document.TryGetFloat(name, out value))
                return true;

            ShaderLabProperty? property = shader.FindProperty(name);
            if (property is { DefaultValue.Length: > 0 })
            {
                value = property.DefaultValue[0];
                return true;
            }

            value = 0.0f;
            return false;
        }

        private EBlendingFactor ReadBlendFactor(ShaderLabCommand command, string argument)
        {
            if (TryReadNumber(argument, out float number))
            {
                return (int)MathF.Round(number) switch
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
                    _ => WarnFactor(command, argument),
                };
            }

            return argument.ToLowerInvariant() switch
            {
                "zero" => EBlendingFactor.Zero,
                "one" => EBlendingFactor.One,
                "dstcolor" => EBlendingFactor.DstColor,
                "srccolor" => EBlendingFactor.SrcColor,
                "oneminusdstcolor" => EBlendingFactor.OneMinusDstColor,
                "srcalpha" => EBlendingFactor.SrcAlpha,
                "oneminussrccolor" => EBlendingFactor.OneMinusSrcColor,
                "dstalpha" => EBlendingFactor.DstAlpha,
                "oneminusdstalpha" => EBlendingFactor.OneMinusDstAlpha,
                "srcalphasaturate" => EBlendingFactor.SrcAlphaSaturate,
                "oneminussrcalpha" => EBlendingFactor.OneMinusSrcAlpha,
                _ => WarnFactor(command, argument),
            };
        }

        private EBlendEquationMode ReadBlendOperation(ShaderLabCommand command, string argument)
        {
            if (TryReadNumber(argument, out float number))
            {
                return (int)MathF.Round(number) switch
                {
                    0 => EBlendEquationMode.FuncAdd,
                    1 => EBlendEquationMode.FuncSubtract,
                    2 => EBlendEquationMode.FuncReverseSubtract,
                    3 => EBlendEquationMode.Min,
                    4 => EBlendEquationMode.Max,
                    _ => WarnOperation(command, argument),
                };
            }

            return argument.ToLowerInvariant() switch
            {
                "add" => EBlendEquationMode.FuncAdd,
                "sub" => EBlendEquationMode.FuncSubtract,
                "revsub" => EBlendEquationMode.FuncReverseSubtract,
                "min" => EBlendEquationMode.Min,
                "max" => EBlendEquationMode.Max,
                _ => WarnOperation(command, argument),
            };
        }

        private bool TryReadNumber(string argument, out float value)
        {
            if (argument.Length > 2 && argument[0] == '[' && argument[^1] == ']')
            {
                string property = argument[1..^1];
                if (TryReadProperty(property, out value))
                    return true;

                warnings.Add($"Shader '{shader.Name}': the render state property '{property}' has no value. The Unity default is used.");
                return false;
            }

            return float.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string FirstArgument(ShaderLabCommand command)
            => command.Arguments.Count > 0 ? command.Arguments[0] : string.Empty;

        /// <summary>Splits arguments at commas, for forms such as <c>Blend One One, Zero One</c>.</summary>
        private static List<string> SplitArguments(ShaderLabCommand command)
        {
            List<string> parts = new(command.Arguments.Count);
            foreach (string argument in command.Arguments)
                foreach (string part in argument.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    parts.Add(part);

            return parts;
        }

        private void Warn(ShaderLabCommand command, string argument)
            => warnings.Add($"Shader '{shader.Name}' line {command.Line}: '{command.Name} {argument}' is not supported. The Unity default is used.");

        private EBlendingFactor WarnFactor(ShaderLabCommand command, string argument)
        {
            Warn(command, argument);
            return EBlendingFactor.One;
        }

        private EBlendEquationMode WarnOperation(ShaderLabCommand command, string argument)
        {
            Warn(command, argument);
            return EBlendEquationMode.FuncAdd;
        }
    }
}
