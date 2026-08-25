using System.Runtime.InteropServices;

namespace XREngine.LocalAgentBroker.Tray;

/// <summary>Preserves a rich text view while response text is appended.</summary>
internal static class RichTextBoxScrollHelper
{
    private const int EmGetScrollPosition = 0x04DD;
    private const int EmSetScrollPosition = 0x04DE;
    private const int WmSetRedraw = 0x000B;

    public static Point Capture(RichTextBox textBox)
    {
        var position = new Point();
        _ = SendMessage(textBox.Handle, EmGetScrollPosition, IntPtr.Zero, ref position);
        return position;
    }

    public static void Restore(RichTextBox textBox, Point position)
        => _ = SendMessage(textBox.Handle, EmSetScrollPosition, IntPtr.Zero, ref position);

    public static bool IsNearBottom(RichTextBox textBox, int thresholdPixels = 40)
    {
        if (textBox.TextLength == 0)
            return true;

        Point endPosition = textBox.GetPositionFromCharIndex(textBox.TextLength - 1);
        return endPosition.Y >= -thresholdPixels
            && endPosition.Y <= textBox.ClientSize.Height + thresholdPixels;
    }

    public static Point BottomPosition(RichTextBox textBox)
    {
        Point current = Capture(textBox);
        if (textBox.TextLength == 0)
            return current;

        Point endPosition = textBox.GetPositionFromCharIndex(textBox.TextLength - 1);
        int overflow = Math.Max(
            0,
            endPosition.Y + textBox.Font.Height + textBox.Margin.Vertical - textBox.ClientSize.Height);
        return new Point(current.X, current.Y + overflow);
    }

    public static void SetRedraw(RichTextBox textBox, bool enabled)
        => _ = SendMessage(
            textBox.Handle,
            WmSetRedraw,
            enabled ? new IntPtr(1) : IntPtr.Zero,
            IntPtr.Zero);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        ref Point longParameter);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter);

}
