using System.Globalization;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Formats the labels that the Workbench shows. Call these only when a model part rebuilds, never in draw code.
/// </summary>
public static class RenderPipelineWorkbenchText
{
    /// <summary>
    /// Returns the display name of <paramref name="command"/> with its branch label or condition variable, when it has one.
    /// </summary>
    public static string GetCommandLabel(ViewportRenderCommand command)
    {
        string name = RenderCommandInfo.Get(command).DisplayName;
        string? detail = GetCommandDetail(command);
        return string.IsNullOrWhiteSpace(detail) ? name : string.Concat(name, " [", detail, "]");
    }

    /// <summary>
    /// Returns the branch label or the condition variable of a flow command, or null.
    /// </summary>
    public static string? GetCommandDetail(ViewportRenderCommand command)
        => command switch
        {
            VPRC_IfElse ifElse => ifElse.Label,
            VPRC_Switch sw => sw.Label,
            VPRC_ConditionalRender conditional => conditional.VariableName,
            _ => null,
        };

    /// <summary>Returns the count as text with the invariant culture.</summary>
    public static string FormatCount(int value)
        => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Returns a byte count as text, for example <c>12.5 MiB</c>.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            return "?";
        if (bytes < 1024)
            return string.Concat(bytes.ToString(CultureInfo.InvariantCulture), " B");

        double value = bytes;
        string[] units = ["KiB", "MiB", "GiB", "TiB"];
        int unit = -1;
        do
        {
            value /= 1024.0;
            unit++;
        }
        while (value >= 1024.0 && unit < units.Length - 1);

        return string.Concat(value.ToString("0.##", CultureInfo.InvariantCulture), " ", units[unit]);
    }

    /// <summary>Returns milliseconds as text with two decimals.</summary>
    public static string FormatMilliseconds(double milliseconds)
        => string.Concat(milliseconds.ToString("0.00", CultureInfo.InvariantCulture), " ms");
}
