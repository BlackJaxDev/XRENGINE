using XREngine.Rendering.UI;

namespace XREngine.Editor.UI.Components
{
    public partial class ConsolePanel : EditorPanel
    {
        public TraceListener TraceListener { get; } = new TraceListener();
        public UITextComponent? Text { get; private set; }

        public ConsolePanel()
        {
            SceneNode.NewChild<UITextComponent>(out var text);
            text.WrapMode = Rendering.FontGlyphSet.EWrapMode.Character;
            text.HideOverflow = true;
            text.FontSize = 14;
            text.VerticalAlignment = EVerticalAlignment.Top;
            text.HorizontalAlignment = EHorizontalAlignment.Left;
            text.Text = string.Empty;
            Text = text;
        }

        private void Output(string? message)
        {
            if (Text != null)
                Text.Text += message;
        }
        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            TraceListener.TraceListenerEvent += Output;
        }
        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            TraceListener.TraceListenerEvent -= Output;
        }
    }
}
