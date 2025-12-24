using XREngine.Data.Colors;
using XREngine.Rendering.UI;

namespace XREngine.Editor.UI
{
    public static partial class EditorUI
    {
        public static class Styles
        {
            public static ColorF4 PropertyNameTextColor { get; set; } = ColorF4.White;
            public static ColorF4 PropertyInputTextColor { get; set; } = ColorF4.White;
            public static ColorF4 ButtonBackgroundColor { get; set; } = ColorF4.Transparent;
            public static ColorF4 ButtonHighlightBackgroundColor { get; set; } = ColorF4.LightGray;
            public static ColorF4 ButtonTextColor { get; set; } = ColorF4.White;
            public static ColorF4 ButtonHighlightTextColor { get; set; } = ColorF4.Black;
            public static float? PropertyInputFontSize { get; set; } = 14;
            public static float PropertyNameFontSize { get; set; } = 14;

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
