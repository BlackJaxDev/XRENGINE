using XREngine.Data.Colors;
using XREngine.Rendering.UI;

namespace XREngine.Editor.UI
{
    public static partial class EditorUI
    {
        /// <summary>
        /// Colors and sizes for editor controls that set their colors directly instead of through a
        /// <see cref="UIStyle"/>. The defaults come from <see cref="EditorGlassTheme"/>, so these controls
        /// match the glass panels.
        /// </summary>
        public static class Styles
        {
            // --- General button styles ---
            public static ColorF4 PropertyNameTextColor { get; set; } = EditorGlassTheme.TextSecondary;
            public static ColorF4 PropertyInputTextColor { get; set; } = EditorGlassTheme.TextPrimary;
            public static ColorF4 ButtonBackgroundColor { get; set; } = ColorF4.Transparent;
            public static ColorF4 ButtonHighlightBackgroundColor { get; set; } = EditorGlassTheme.ButtonHoverFill;
            public static ColorF4 ButtonTextColor { get; set; } = EditorGlassTheme.TextPrimary;
            public static ColorF4 ButtonHighlightTextColor { get; set; } = EditorGlassTheme.TextPrimary;
            public static float? PropertyInputFontSize { get; set; } = 14;
            public static float PropertyNameFontSize { get; set; } = 14;

            // --- Hierarchy panel styles ---
            public static ColorF4 SelectedRowColor { get; set; } = EditorGlassTheme.RowSelectedFill;
            public static ColorF4 HoverRowColor { get; set; } = EditorGlassTheme.RowHoverFill;
            public static ColorF4 ExpandArrowColor { get; set; } = EditorGlassTheme.TextSecondary;
            public static ColorF4 SectionHeaderColor { get; set; } = new(0.18f, 0.18f, 0.22f, 0.85f);
            public static ColorF4 SectionHeaderTextColor { get; set; } = EditorGlassTheme.TextSecondary;
            public static ColorF4 WorldHeaderColor { get; set; } = new(0.12f, 0.12f, 0.16f, 0.9f);
            public static ColorF4 DisabledTextColor { get; set; } = EditorGlassTheme.TextDisabled;
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
