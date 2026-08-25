using System.Runtime.InteropServices;

namespace XREngine.LocalAgentBroker.Tray;

/// <summary>Preserves a rich text view while response text is appended.</summary>
internal static class RichTextBoxScrollHelper
{
    private const int EmGetScrollPosition = 0x04DD;
    private const int EmSetScrollPosition = 0x04DE;

    public static Point Capture(RichTextBox textBox)
    {
        var position = new Point();
        _ = SendMessage(textBox.Handle, EmGetScrollPosition, IntPtr.Zero, ref position);
        return position;
    }

    public static void Restore(RichTextBox textBox, Point position)
        => _ = SendMessage(textBox.Handle, EmSetScrollPosition, IntPtr.Zero, ref position);

    public static bool IsAtBottom(RichTextBox textBox)
    {
        if (textBox.TextLength == 0)
            return true;

        Point endPosition = textBox.GetPositionFromCharIndex(textBox.TextLength - 1);
        return endPosition.Y >= 0 && endPosition.Y < textBox.ClientSize.Height;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        ref Point longParameter);
}
