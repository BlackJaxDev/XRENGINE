using XREngine.Data.Rendering;

namespace XREngine.Rendering.Diagnostics;

/// <summary>
/// Immutable diagnostic payload declaration attached to one instrumented GPU pass.
/// The source resource is deliberately backend-owned; this node only describes the
/// bounded payload and the telemetry decoder that may consume it after retirement.
/// </summary>
public readonly record struct GpuDiagnosticReadbackPlanNode(
    ulong PassIdentity,
    uint ViewId,
    uint SourceByteOffset,
    uint ByteCount,
    EMeshSubmissionStrategy Strategy,
    EGpuDiagnosticReadbackDecoder Decoder)
{
    /// <summary>Whether this node is eligible for a diagnostic sidecar.</summary>
    public bool IsInstrumentedPass => GpuDiagnosticReadbackPlan.IsInstrumented(Strategy);

    /// <summary>
    /// Compatibility label for diagnostic consumers. Values are interned
    /// literals, so reading this property creates no per-frame string.
    /// </summary>
    public string DecoderKey => Decoder switch
    {
        EGpuDiagnosticReadbackDecoder.IndirectDrawCount => "IndirectDrawCount",
        EGpuDiagnosticReadbackDecoder.MeshletVisibility => "MeshletVisibility",
        EGpuDiagnosticReadbackDecoder.SubmissionValidation => "SubmissionValidation",
        _ => "None",
    };

    internal void Validate()
    {
        if (!IsInstrumentedPass)
        {
            throw new InvalidOperationException(
                $"Diagnostic readback cannot attach to the {Strategy} submission strategy.");
        }

        if (ByteCount == 0)
            throw new InvalidOperationException("Diagnostic readback nodes must declare a non-zero payload size.");
        if (Decoder == EGpuDiagnosticReadbackDecoder.None)
            throw new InvalidOperationException("Diagnostic readback nodes must declare a telemetry decoder key.");
    }
}
