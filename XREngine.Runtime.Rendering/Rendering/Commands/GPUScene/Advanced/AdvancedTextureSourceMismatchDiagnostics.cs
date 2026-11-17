using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Process totals of texture source mismatches (<see cref="EAdvancedTextureSourceMismatch"/>)
/// that a backend source check found, and the last reason. The Vulkan runtime counts each
/// failed check of a canonical package (admission or lowering). The MCP render pipeline readout
/// and the Advanced preparation SUMMARY line read the totals. Counting does not allocate.
/// </summary>
public static class AdvancedTextureSourceMismatchDiagnostics
{
    private static readonly long[] s_counts = new long[(int)EAdvancedTextureSourceMismatch.Count];
    private static int s_last;

    /// <summary>The reason of the last counted mismatch, or None.</summary>
    public static EAdvancedTextureSourceMismatch Last
        => (EAdvancedTextureSourceMismatch)Volatile.Read(ref s_last);

    /// <summary>Counts one mismatch and records it as the last reason.</summary>
    public static void Note(EAdvancedTextureSourceMismatch reason)
    {
        if (reason <= EAdvancedTextureSourceMismatch.None || reason >= EAdvancedTextureSourceMismatch.Count)
            return;
        Interlocked.Increment(ref s_counts[(int)reason]);
        Volatile.Write(ref s_last, (int)reason);
    }

    /// <summary>Process total for <paramref name="reason"/>.</summary>
    public static long GetCount(EAdvancedTextureSourceMismatch reason)
        => reason > EAdvancedTextureSourceMismatch.None && reason < EAdvancedTextureSourceMismatch.Count
            ? Interlocked.Read(ref s_counts[(int)reason])
            : 0L;

    /// <summary>Appends " srcMismatch.&lt;reason&gt;=&lt;total&gt;" for each reason with a nonzero total.</summary>
    public static void AppendTotals(StringBuilder builder)
    {
        for (EAdvancedTextureSourceMismatch reason = EAdvancedTextureSourceMismatch.RecordImageMissing;
            reason < EAdvancedTextureSourceMismatch.Count;
            ++reason)
        {
            long count = GetCount(reason);
            if (count != 0)
                builder.Append(" srcMismatch.").Append(reason).Append('=').Append(count);
        }
    }

    /// <summary>Returns the nonzero totals by reason name, for diagnostics readouts.</summary>
    public static Dictionary<string, long> CaptureTotals()
    {
        Dictionary<string, long> totals = [];
        for (EAdvancedTextureSourceMismatch reason = EAdvancedTextureSourceMismatch.RecordImageMissing;
            reason < EAdvancedTextureSourceMismatch.Count;
            ++reason)
        {
            long count = GetCount(reason);
            if (count != 0)
                totals[reason.ToString()] = count;
        }
        return totals;
    }
}
