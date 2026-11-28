using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Rendering.UI;

namespace XREngine.Editor.UI;

/// <summary>
/// Visual theme for the native editor: dark frosted-glass panels over the scene, hairline
/// borders, rounded corners, and spacious controls. Panel and control factories read every
/// color, radius, blur, and spacing value from this type, so one edit here restyles the
/// menu bar, the dock panels, the hierarchy, and the inspector together.
/// </summary>
/// <remarks>
/// Colors are authored in sRGB. Styles set <see cref="UIStyleProperties.DecodeSrgbColors"/>,
/// so the batched quad shader converts them to linear before it blends. Alpha is linear coverage, and
/// blending happens in linear space. A white overlay therefore looks much brighter than the same alpha
/// in a web page, so the white overlay fills below use small alpha values.
/// </remarks>
public static class EditorGlassTheme
{
    // --- Palette ---

    /// <summary>Accent for selection, focus, and primary actions.</summary>
    public static ColorF4 Accent { get; set; } = Srgb(10, 132, 255);
    /// <summary>Main text on glass.</summary>
    public static ColorF4 TextPrimary { get; set; } = new(0.96f, 0.97f, 0.99f, 0.96f);
    /// <summary>Labels, captions, and other secondary text.</summary>
    public static ColorF4 TextSecondary { get; set; } = new(0.80f, 0.83f, 0.90f, 0.70f);
    /// <summary>Text for disabled controls.</summary>
    public static ColorF4 TextDisabled { get; set; } = new(0.80f, 0.83f, 0.90f, 0.38f);
    /// <summary>Text for a warning or a failed action, such as a component that could not be added.</summary>
    public static ColorF4 TextWarning { get; set; } = Srgb(255, 159, 10, 0.92f);

    /// <summary>Top fill of a panel card. The bottom fill is a little darker.</summary>
    public static ColorF4 PanelFillTop { get; set; } = Srgb(30, 33, 42, 0.56f);
    /// <summary>Bottom fill of a panel card.</summary>
    public static ColorF4 PanelFillBottom { get; set; } = Srgb(20, 22, 29, 0.66f);
    /// <summary>Hairline border of a panel card.</summary>
    public static ColorF4 PanelBorder { get; set; } = new(1.0f, 1.0f, 1.0f, 0.07f);
    /// <summary>Soft shadow under a panel card.</summary>
    public static ColorF4 PanelShadow { get; set; } = new(0.0f, 0.0f, 0.0f, 0.34f);

    /// <summary>Top color of the window backdrop behind the panel cards.</summary>
    public static ColorF4 WindowBackdropTop { get; set; } = Srgb(38, 44, 62);
    /// <summary>Bottom color of the window backdrop behind the panel cards.</summary>
    public static ColorF4 WindowBackdropBottom { get; set; } = Srgb(14, 16, 24);

    /// <summary>Fill of the menu bar across the top of the window.</summary>
    public static ColorF4 MenuBarFill { get; set; } = Srgb(18, 20, 26, 0.64f);
    /// <summary>Fill of a drop-down menu.</summary>
    public static ColorF4 MenuFill { get; set; } = Srgb(26, 29, 38, 0.97f);

    /// <summary>Resting fill of a button. Most buttons rest almost clear on the glass.</summary>
    public static ColorF4 ButtonFill { get; set; } = new(1.0f, 1.0f, 1.0f, 0.014f);
    /// <summary>Button fill under the pointer.</summary>
    public static ColorF4 ButtonHoverFill { get; set; } = new(1.0f, 1.0f, 1.0f, 0.04f);
    /// <summary>Button fill while pressed.</summary>
    public static ColorF4 ButtonPressedFill { get; set; } = new(1.0f, 1.0f, 1.0f, 0.008f);

    /// <summary>Row fill under the pointer in lists and trees.</summary>
    public static ColorF4 RowHoverFill { get; set; } = new(1.0f, 1.0f, 1.0f, 0.022f);
    /// <summary>Row fill for the current selection.</summary>
    public static ColorF4 RowSelectedFill { get; set; } = Srgb(10, 132, 255, 0.24f);

    /// <summary>Fill of a text field or other input well.</summary>
    public static ColorF4 FieldFill { get; set; } = new(0.0f, 0.0f, 0.0f, 0.22f);
    /// <summary>Border of an input well.</summary>
    public static ColorF4 FieldBorder { get; set; } = new(1.0f, 1.0f, 1.0f, 0.05f);

    // --- Metrics, in logical pixels ---

    /// <summary>Corner radius of panel cards.</summary>
    public static float PanelRadius { get; set; } = 12.0f;
    /// <summary>Gap between neighbouring panel cards and between a card and the window edge.</summary>
    public static float PanelGutter { get; set; } = 6.0f;
    /// <summary>Space between a card edge and its content.</summary>
    public static float PanelPadding { get; set; } = 10.0f;
    /// <summary>Backdrop blur radius behind panel cards.</summary>
    public static float PanelBlurRadius { get; set; } = 24.0f;
    /// <summary>Corner radius of buttons, tabs, and row highlights.</summary>
    public static float ControlRadius { get; set; } = 7.0f;
    /// <summary>Height of a standard button.</summary>
    public static float ButtonHeight { get; set; } = 28.0f;
    /// <summary>Height of a tree or list row.</summary>
    public static float RowHeight { get; set; } = 30.0f;
    /// <summary>Height of a panel header with its tab strip.</summary>
    public static float HeaderHeight { get; set; } = 34.0f;
    /// <summary>Height of the menu bar.</summary>
    public static float MenuBarHeight { get; set; } = 34.0f;
    /// <summary>Text size for body text and controls.</summary>
    public static float FontSize { get; set; } = 14.0f;
    /// <summary>Text size for captions and section titles.</summary>
    public static float CaptionFontSize { get; set; } = 12.0f;
    /// <summary>Width of one tab in a panel header.</summary>
    public static float TabWidth { get; set; } = 92.0f;
    /// <summary>Height of one tab in a panel header.</summary>
    public static float TabHeight { get; set; } = 26.0f;

    /// <summary>
    /// Gets or sets whether the 3D scene renders behind the panel cards. The pipeline backdrop blur samples the
    /// scene, so the cards blur only when the scene is what lies behind them. Over the plain window backdrop the
    /// cards use their translucent fill without blur.
    /// </summary>
    public static bool SceneBehindPanels { get; set; }

    // --- Styles ---

    /// <summary>Frosted card behind one dock panel stack.</summary>
    public static UIStyleProperties PanelCard => new()
    {
        BackgroundColor = PanelFillTop,
        GradientBottomColor = PanelFillBottom,
        CornerRadii = new Vector4(PanelRadius),
        CornerExponent = 2.6f,
        BorderWidth = 1.0f,
        BorderColor = PanelBorder,
        BorderHighlightBoost = 0.12f,
        ShadowColor = PanelShadow,
        ShadowParams = new Vector4(0.0f, 6.0f, 18.0f, -2.0f),
        BackdropBlurRadius = SceneBehindPanels ? PanelBlurRadius : null,
        BackdropVibrancySaturation = 1.3f,
        BackdropVibrancyBrightness = 0.92f,
        DecodeSrgbColors = true,
    };

    /// <summary>Card of a stack that shows the scene behind the UI: no fill, so the scene shows through.</summary>
    public static UIStyleProperties ClearCard => new()
    {
        BackgroundColor = new ColorF4(0.0f, 0.0f, 0.0f, 0.0f),
        DecodeSrgbColors = true,
    };

    /// <summary>Soft vertical gradient that fills the window behind the panel cards.</summary>
    public static UIStyleProperties WindowBackdrop => new()
    {
        BackgroundColor = WindowBackdropTop,
        GradientBottomColor = WindowBackdropBottom,
        DecodeSrgbColors = true,
    };

    /// <summary>Thin divider line inside menus and panels.</summary>
    public static UIStyleProperties Divider => new()
    {
        BackgroundColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.03f),
        DecodeSrgbColors = true,
    };

    /// <summary>Frosted bar across the top of the window.</summary>
    public static UIStyleProperties MenuBar => new()
    {
        BackgroundColor = MenuBarFill,
        BorderWidth = 1.0f,
        BorderColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.04f),
        BackdropBlurRadius = SceneBehindPanels ? PanelBlurRadius : null,
        BackdropVibrancySaturation = 1.25f,
        BackdropVibrancyBrightness = 0.9f,
        DecodeSrgbColors = true,
    };

    /// <summary>Frosted drop-down or context menu surface.</summary>
    public static UIStyleProperties MenuSurface => new()
    {
        BackgroundColor = MenuFill,
        CornerRadii = new Vector4(10.0f),
        CornerExponent = 2.6f,
        BorderWidth = 1.0f,
        BorderColor = PanelBorder,
        ShadowColor = new ColorF4(0.0f, 0.0f, 0.0f, 0.45f),
        ShadowParams = new Vector4(0.0f, 10.0f, 24.0f, -4.0f),
        // A menu often opens over another panel. The pipeline blur samples the scene, not that panel,
        // so a menu uses a dense fill instead of blur.
        DecodeSrgbColors = true,
    };

    /// <summary>Rounded input well for text fields.</summary>
    public static UIStyleProperties Field => new()
    {
        BackgroundColor = FieldFill,
        CornerRadii = new Vector4(ControlRadius),
        BorderWidth = 1.0f,
        BorderColor = FieldBorder,
        DecodeSrgbColors = true,
    };

    /// <summary>Quiet rounded button: almost clear at rest, brighter on hover and focus.</summary>
    public static UIStyle Button => new()
    {
        Base = new UIStyleProperties
        {
            BackgroundColor = ButtonFill,
            TextColor = TextPrimary,
            CornerRadii = new Vector4(ControlRadius),
            DecodeSrgbColors = true,
        },
        Hover = new UIStyleProperties { BackgroundColor = ButtonHoverFill },
        Focused = new UIStyleProperties { BackgroundColor = ButtonHoverFill, BorderWidth = 1.0f, BorderColor = Accent },
        Pressed = new UIStyleProperties { BackgroundColor = ButtonPressedFill, VisualScale = new Vector2(0.98f, 0.98f) },
        ImmediateHighlight = true,
        ColorTransition = UITransitionSpec.AppleEaseOut(0.12f),
        TransformTransition = UITransitionSpec.AppleEaseOut(0.12f),
    };

    /// <summary>Menu bar and menu item button: clear at rest, rounded highlight on hover.</summary>
    public static UIStyle MenuItem => new()
    {
        Base = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.0f),
            TextColor = TextPrimary,
            CornerRadii = new Vector4(6.0f),
            DecodeSrgbColors = true,
        },
        Hover = new UIStyleProperties { BackgroundColor = ButtonHoverFill },
        Focused = new UIStyleProperties { BackgroundColor = ButtonHoverFill },
        Pressed = new UIStyleProperties { BackgroundColor = RowSelectedFill },
        ImmediateHighlight = true,
        ColorTransition = UITransitionSpec.AppleEaseOut(0.10f),
    };

    /// <summary>Tree and list row: clear at rest, soft pill on hover.</summary>
    public static UIStyle Row => new()
    {
        Base = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.0f),
            TextColor = TextPrimary,
            CornerRadii = new Vector4(ControlRadius),
            DecodeSrgbColors = true,
        },
        Hover = new UIStyleProperties { BackgroundColor = RowHoverFill },
        Focused = new UIStyleProperties { BackgroundColor = RowHoverFill },
        Pressed = new UIStyleProperties { BackgroundColor = RowSelectedFill },
        ImmediateHighlight = true,
        ColorTransition = UITransitionSpec.AppleEaseOut(0.10f),
    };

    /// <summary>Panel header tab: clear at rest, soft pill on hover.</summary>
    public static UIStyle Tab => new()
    {
        Base = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.0f),
            TextColor = TextSecondary,
            CornerRadii = new Vector4(ControlRadius),
            DecodeSrgbColors = true,
        },
        Hover = new UIStyleProperties { BackgroundColor = RowHoverFill, TextColor = TextPrimary },
        Focused = new UIStyleProperties { BackgroundColor = RowHoverFill, BorderWidth = 1.0f, BorderColor = Accent },
        Pressed = new UIStyleProperties { BackgroundColor = ButtonPressedFill },
        ImmediateHighlight = true,
        ColorTransition = UITransitionSpec.AppleEaseOut(0.10f),
    };

    /// <summary>Panel header tab of the shown panel: a raised pill with bright text.</summary>
    public static UIStyle SelectedTab => new()
    {
        Base = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.045f),
            TextColor = TextPrimary,
            CornerRadii = new Vector4(ControlRadius),
            BorderWidth = 1.0f,
            BorderColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.04f),
            DecodeSrgbColors = true,
        },
        Hover = new UIStyleProperties { BackgroundColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.06f) },
        Focused = new UIStyleProperties { BorderColor = Accent },
        ImmediateHighlight = true,
        ColorTransition = UITransitionSpec.AppleEaseOut(0.10f),
    };

    /// <summary>
    /// Sets the static styles of runtime UI controls that the editor uses, such as the context menu surface of
    /// popups, combo boxes, and tooltips, to the glass theme. The editor UI calls this before it builds panels.
    /// </summary>
    public static void ApplyRuntimeControlStyles()
    {
        UIContextMenuComponent.BackgroundColor = MenuFill;
        UIContextMenuComponent.BackgroundQuadStyle = MenuSurface.ToQuadStyle();
        // Item quads have no sRGB decode, so this is the accent color in linear values.
        UIContextMenuComponent.ItemHighlightBackground = new ColorF4(0.003f, 0.23f, 1.0f, 0.5f);
        UIContextMenuComponent.ItemDefaultTextColor = TextPrimary;
    }

    /// <summary>Applies <paramref name="style"/> to a quad without an animator.</summary>
    public static void ApplyStatic(UIMaterialComponent quad, in UIStyleProperties style)
    {
        ColorF4 fill = style.BackgroundColor ?? new ColorF4(0.0f, 0.0f, 0.0f, 0.0f);
        quad.SetBatchSolidColor(new Vector4(fill.R, fill.G, fill.B, fill.A));
        quad.QuadStyle = style.ToQuadStyle();
    }

    private static ColorF4 Srgb(byte r, byte g, byte b, float a = 1.0f)
        => new(r / 255.0f, g / 255.0f, b / 255.0f, a);
}
