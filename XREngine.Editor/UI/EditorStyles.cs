using XREngine.Data.Colors;
using XREngine.Rendering.UI;

namespace XREngine.Editor.UI
{
    public static partial class EditorUI
    {
        public static class Styles
        {
            // --- General button styles ---
            public static ColorF4 PropertyNameTextColor { get; set; } = ColorF4.White;
            public static ColorF4 PropertyInputTextColor { get; set; } = ColorF4.White;
            public static ColorF4 ButtonBackgroundColor { get; set; } = ColorF4.Transparent;
            public static ColorF4 ButtonHighlightBackgroundColor { get; set; } = ColorF4.LightGray;
            public static ColorF4 ButtonTextColor { get; set; } = ColorF4.White;
            public static ColorF4 ButtonHighlightTextColor { get; set; } = ColorF4.Black;
            public static float? PropertyInputFontSize { get; set; } = 14;
            public static float PropertyNameFontSize { get; set; } = 14;

            // --- Hierarchy panel styles (Phase 9) ---
            public static ColorF4 SelectedRowColor { get; set; } = new(0.22f, 0.50f, 0.85f, 0.45f);
            public static ColorF4 HoverRowColor { get; set; } = new(0.30f, 0.30f, 0.30f, 0.25f);
            public static ColorF4 ExpandArrowColor { get; set; } = new(0.75f, 0.75f, 0.75f, 1.0f);
            public static ColorF4 SectionHeaderColor { get; set; } = new(0.18f, 0.18f, 0.22f, 0.85f);
            public static ColorF4 SectionHeaderTextColor { get; set; } = new(0.9f, 0.9f, 0.9f, 1.0f);
            public static ColorF4 WorldHeaderColor { get; set; } = new(0.12f, 0.12f, 0.16f, 0.9f);
            public static ColorF4 DisabledTextColor { get; set; } = new(0.55f, 0.55f, 0.55f, 1.0f);
            public static ColorF4 DirtyIndicatorColor { get; set; } = new(0.9f, 0.7f, 0.2f, 1.0f);
            public static ColorF4 HiddenSceneWarningColor { get; set; } = new(0.9f, 0.7f, 0.2f, 1.0f);
            public static float HierarchyFontSize { get; set; } = 14.0f;
            public static float HierarchyRowHeight { get; set; } = 30.0f;
            public static float DepthIndent { get; set; } = 10.0f;
            public static float SectionHeaderFontSize { get; set; } = 13.0f;
            public static float SectionHeaderHeight { get; set; } = 28.0f;

            public static void UpdateButton(UIButtonComponent button)
            {
                button.DefaultBackgroundColor = ButtonBackgroundColor;
                button.HighlightBackgroundColor = ButtonHighlightBackgroundColor;
                button.DefaultTextColor = ButtonTextColor;
                button.HighlightTextColor = ButtonHighlightTextColor;
            }
        }
    }
}
