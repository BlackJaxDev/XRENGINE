namespace XREngine.Editor.MaterialAuthoring;

public enum EShaderAuthoringWidgetCapability
{
    Scalar,
    Vector,
    Color,
    Texture,
    TextureArray,
    Gradient,
    Curve,
    Mask,
    Action,
    Tool,
    Decorator,
}

public sealed record ShaderAuthoringWidgetDescriptor(
    string Id,
    EShaderAuthoringWidgetCapability Capability,
    bool SupportsMixedValues,
    bool SupportsReset,
    bool SupportsAnimation,
    bool IsTool = false);

/// <summary>
/// Closed registry of annotation IDs accepted by the native material editor.
/// Metadata cannot add executable types; external registrations require an
/// engine-owned ID and delegate registration in editor startup code.
/// </summary>
public static class ShaderAuthoringWidgetRegistry
{
    private static readonly Dictionary<string, ShaderAuthoringWidgetDescriptor> Widgets =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Header"] = new("Header", EShaderAuthoringWidgetCapability.Decorator, true, false, false),
            ["ThryRichLabel"] = new("ThryRichLabel", EShaderAuthoringWidgetCapability.Decorator, true, false, false),
            ["Gamma"] = new("Gamma", EShaderAuthoringWidgetCapability.Scalar, true, true, true),
            ["HDR"] = new("HDR", EShaderAuthoringWidgetCapability.Color, true, true, true),
            ["Normal"] = new("Normal", EShaderAuthoringWidgetCapability.Texture, true, true, false),
            ["NoScaleOffset"] = new("NoScaleOffset", EShaderAuthoringWidgetCapability.Texture, true, true, false),
            ["NonModifiableTextureData"] = new("NonModifiableTextureData", EShaderAuthoringWidgetCapability.Texture, true, false, false),
            ["HideInInspector"] = new("HideInInspector", EShaderAuthoringWidgetCapability.Decorator, true, false, false),
            ["DoNotAnimate"] = new("DoNotAnimate", EShaderAuthoringWidgetCapability.Decorator, true, false, false),
            ["DoNotLock"] = new("DoNotLock", EShaderAuthoringWidgetCapability.Decorator, true, false, false),
            ["DoNotRename"] = new("DoNotRename", EShaderAuthoringWidgetCapability.Decorator, true, false, false),
            ["lilToggleLeft"] = new("lilToggleLeft", EShaderAuthoringWidgetCapability.Scalar, true, true, true),            ["Enum"] = new("Enum", EShaderAuthoringWidgetCapability.Scalar, true, true, true),
            ["KeywordEnum"] = new("KeywordEnum", EShaderAuthoringWidgetCapability.Scalar, true, true, false),
            ["Toggle"] = new("Toggle", EShaderAuthoringWidgetCapability.Scalar, true, true, true),
            ["ToggleUI"] = new("ToggleUI", EShaderAuthoringWidgetCapability.Scalar, true, true, true),
            ["MaterialToggle"] = new("MaterialToggle", EShaderAuthoringWidgetCapability.Scalar, true, true, true),
            ["ThryWideEnum"] = new("ThryWideEnum", EShaderAuthoringWidgetCapability.Scalar, true, true, true),
            ["ThryToggle"] = new("ThryToggle", EShaderAuthoringWidgetCapability.Scalar, true, true, true),
            ["ThryToggleUI"] = new("ThryToggleUI", EShaderAuthoringWidgetCapability.Scalar, true, true, true),
            ["PowerSlider"] = new("PowerSlider", EShaderAuthoringWidgetCapability.Scalar, true, true, true),
            ["IntRange"] = new("IntRange", EShaderAuthoringWidgetCapability.Scalar, true, true, true),
            ["MultiSlider"] = new("MultiSlider", EShaderAuthoringWidgetCapability.Vector, true, true, true),
            ["Vector2"] = new("Vector2", EShaderAuthoringWidgetCapability.Vector, true, true, true),
            ["Vector3"] = new("Vector3", EShaderAuthoringWidgetCapability.Vector, true, true, true),
            ["Vector31"] = new("Vector31", EShaderAuthoringWidgetCapability.Vector, true, true, true),
            ["Vector4Toggles"] = new("Vector4Toggles", EShaderAuthoringWidgetCapability.Vector, true, true, true),
            ["VectorLabel"] = new("VectorLabel", EShaderAuthoringWidgetCapability.Vector, true, true, true),
            ["VectorToSliders"] = new("VectorToSliders", EShaderAuthoringWidgetCapability.Vector, true, true, true),
            ["ButtonVector"] = new("ButtonVector", EShaderAuthoringWidgetCapability.Vector, true, true, true),
            ["ThryMultiFloatButtons"] = new("ThryMultiFloatButtons", EShaderAuthoringWidgetCapability.Vector, true, true, true),
            ["ThryMultiFloats"] = new("ThryMultiFloats", EShaderAuthoringWidgetCapability.Vector, true, true, true),
            ["ThryMask"] = new("ThryMask", EShaderAuthoringWidgetCapability.Mask, true, true, true),
            ["ThryTexture"] = new("ThryTexture", EShaderAuthoringWidgetCapability.Texture, true, true, true),
            ["TextureKeyword"] = new("TextureKeyword", EShaderAuthoringWidgetCapability.Texture, true, true, true),
            ["TextureArray"] = new("TextureArray", EShaderAuthoringWidgetCapability.TextureArray, true, true, true),
            ["Gradient"] = new("Gradient", EShaderAuthoringWidgetCapability.Gradient, false, true, true),
            ["Curve"] = new("Curve", EShaderAuthoringWidgetCapability.Curve, false, true, true),
            ["FourFloatCurve"] = new("FourFloatCurve", EShaderAuthoringWidgetCapability.Curve, false, true, true),
            ["Curve4"] = new("Curve4", EShaderAuthoringWidgetCapability.Curve, false, true, true),
            ["Ramp4"] = new("Ramp4", EShaderAuthoringWidgetCapability.Gradient, false, true, true),
            ["ThryHeaderLabel"] = new("ThryHeaderLabel", EShaderAuthoringWidgetCapability.Decorator, true, false, false),
            ["Helpbox"] = new("Helpbox", EShaderAuthoringWidgetCapability.Decorator, true, false, false),
            ["IMPORTANT"] = new("IMPORTANT", EShaderAuthoringWidgetCapability.Decorator, true, false, false),
            ["sRGBWarning"] = new("sRGBWarning", EShaderAuthoringWidgetCapability.Decorator, true, false, false),
            ["Space"] = new("Space", EShaderAuthoringWidgetCapability.Decorator, true, false, false),
            ["ThrySpace"] = new("ThrySpace", EShaderAuthoringWidgetCapability.Decorator, true, false, false),
            ["ThryRGBAPacker"] = new("ThryRGBAPacker", EShaderAuthoringWidgetCapability.Tool, false, true, false, true),
            ["ThryDecalPositioning"] = new("ThryDecalPositioning", EShaderAuthoringWidgetCapability.Tool, false, true, false, true),
            ["ThryShaderOptimizerLockButton"] = new("ThryShaderOptimizerLockButton", EShaderAuthoringWidgetCapability.Tool, true, false, false, true),
        };

    public static bool TryResolve(string? annotation, out ShaderAuthoringWidgetDescriptor descriptor)
    {
        if (annotation is not null && Widgets.TryGetValue(annotation, out ShaderAuthoringWidgetDescriptor? resolved))
        {
            descriptor = resolved;
            return true;
        }

        descriptor = null!;
        return false;
    }

    public static bool IsAllowlistedTool(string? annotation)
        => TryResolve(annotation, out ShaderAuthoringWidgetDescriptor descriptor) && descriptor.IsTool;

    public static IReadOnlyCollection<ShaderAuthoringWidgetDescriptor> All => Widgets.Values;
}
