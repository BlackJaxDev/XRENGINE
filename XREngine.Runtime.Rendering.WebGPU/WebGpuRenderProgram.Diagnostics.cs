using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRenderProgram
{
    private long _modulePreparationStartedAt;
    private long _computePreparationStartedAt;

    /// <summary>Formats cold preparation state only when the caller requests failure diagnostics.</summary>
    internal bool AppendPendingPreparation(StringBuilder output)
    {
        bool modulePending = _preparation is { IsCompleted: false };
        bool computePending = _computePreparation is { IsCompleted: false };
        if (!modulePending && !computePending)
            return false;
        if (output.Length != 0)
            output.Append(" | ");
        output.Append(_artifact?.Name ?? Data.Name ?? "unnamed program");
        output.Append(" module=").Append(_preparation?.Status.ToString() ?? "not requested");
        output.Append(" compute=").Append(_computePreparation?.Status.ToString() ?? "not requested");
        long started = modulePending ? _modulePreparationStartedAt : _computePreparationStartedAt;
        output.Append(" pendingSeconds=").Append(Stopwatch.GetElapsedTime(started).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture));
        return true;
    }
}
