using System.Threading;

namespace XREngine.Rendering;

public partial class HybridRenderingManager
{
    private const int ZeroReadbackMaterialTableDiagnosticPassOffset = 1;
    private const int ZeroReadbackMaterialTableDiagnosticPassCapacity = 64;
    private static readonly int[] s_zeroReadbackMaterialTableStages =
        new int[ZeroReadbackMaterialTableDiagnosticPassCapacity];
    private static readonly long[] s_zeroReadbackMaterialTableFrames =
        new long[ZeroReadbackMaterialTableDiagnosticPassCapacity];

    /// <summary>
    /// Returns the latest fixed per-pass compact material-table gate reached by each pass.
    /// This allocation is diagnostic-only; render-thread publication only performs atomic writes.
    /// </summary>
    public static ZeroReadbackMaterialTablePassDiagnostic[] GetZeroReadbackMaterialTableDiagnosticsSnapshot()
    {
        var result = new ZeroReadbackMaterialTablePassDiagnostic[ZeroReadbackMaterialTableDiagnosticPassCapacity];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = new ZeroReadbackMaterialTablePassDiagnostic(
                index - ZeroReadbackMaterialTableDiagnosticPassOffset,
                (EZeroReadbackMaterialTableDiagnosticStage)Volatile.Read(ref s_zeroReadbackMaterialTableStages[index]),
                unchecked((ulong)Interlocked.Read(ref s_zeroReadbackMaterialTableFrames[index])));
        }

        return result;
    }

    private static void RecordZeroReadbackMaterialTableDiagnostic(
        int renderPass,
        EZeroReadbackMaterialTableDiagnosticStage stage)
    {
        int index = renderPass + ZeroReadbackMaterialTableDiagnosticPassOffset;
        if ((uint)index >= ZeroReadbackMaterialTableDiagnosticPassCapacity)
            return;

        Interlocked.Exchange(ref s_zeroReadbackMaterialTableStages[index], (int)stage);
        Interlocked.Exchange(
            ref s_zeroReadbackMaterialTableFrames[index],
            unchecked((long)RuntimeEngine.Rendering.State.RenderFrameId));
    }
}
