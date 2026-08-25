using System.Runtime.InteropServices;
using Microsoft.Win32;
using XREngine.LocalAgentBroker.Shared;

namespace XREngine.LocalAgentBroker.Tray;

/// <summary>Resolves and applies the broker's light or dark Windows color palette.</summary>
internal static class BrokerTheme
{
    private const int DwmUseImmersiveDarkMode = 20;

    public static bool ResolveDark(BrokerUiThemePreference preference)
        => preference switch
        {
            BrokerUiThemePreference.Light => false,
            BrokerUiThemePreference.Dark => true,
            _ => SystemUsesDarkTheme(),
        };

    public static Color WindowColor(bool dark)
        => dark ? Color.FromArgb(24, 26, 31) : Color.FromArgb(245, 247, 250);

    public static Color SurfaceColor(bool dark)
        => dark ? Color.FromArgb(31, 34, 40) : Color.White;

    public static Color InputColor(bool dark)
        => dark ? Color.FromArgb(40, 44, 52) : Color.White;

    public static Color BorderColor(bool dark)
        => dark ? Color.FromArgb(57, 62, 72) : Color.FromArgb(220, 224, 230);

    public static Color TextColor(bool dark)
        => dark ? Color.FromArgb(224, 228, 235) : Color.FromArgb(38, 43, 52);

    public static Color MutedTextColor(bool dark)
        => dark ? Color.FromArgb(158, 166, 181) : Color.FromArgb(95, 104, 120);

    public static void Apply(Control root, bool dark)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.SuspendLayout();
        try
        {
            ApplyControl(root, dark);
            if (root is Form form)
                ApplyTitleBar(form, dark);
        }
        finally
        {
            root.ResumeLayout(performLayout: true);
        }
    }

    public static void ApplyTitleBar(Form form, bool dark)
    {
        if (!form.IsHandleCreated)
            return;

        int enabled = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(
            form.Handle,
            DwmUseImmersiveDarkMode,
            ref enabled,
            sizeof(int));
    }

    private static void ApplyControl(Control control, bool dark)
    {
        Color surface = SurfaceColor(dark);
        control.BackColor = surface;
        control.ForeColor = TextColor(dark);

        switch (control)
        {
            case Form:
                control.BackColor = WindowColor(dark);
                break;
            case SplitContainer split:
                split.BackColor = BorderColor(dark);
                split.Panel1.BackColor = surface;
                split.Panel2.BackColor = surface;
                break;
            case TextBoxBase:
            case ComboBox:
            case NumericUpDown:
            case ListView:
                control.BackColor = InputColor(dark);
                break;
            case Button button:
                button.UseVisualStyleBackColor = false;
                button.BackColor = InputColor(dark);
                break;
            case ToolStrip toolStrip:
                toolStrip.BackColor = surface;
                toolStrip.ForeColor = TextColor(dark);
                ApplyToolStripItems(toolStrip.Items, dark);
                break;
        }

        foreach (Control child in control.Controls)
            ApplyControl(child, dark);
    }

    private static void ApplyToolStripItems(ToolStripItemCollection items, bool dark)
    {
        foreach (ToolStripItem item in items)
        {
            item.BackColor = SurfaceColor(dark);
            item.ForeColor = TextColor(dark);
            if (item is ToolStripDropDownItem dropDownItem)
                ApplyToolStripItems(dropDownItem.DropDownItems, dark);
        }
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using RegistryKey? personalize = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return personalize?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
