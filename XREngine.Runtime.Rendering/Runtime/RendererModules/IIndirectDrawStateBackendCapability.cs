using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Captures backend-specific state required while recording an indirect draw
/// without exposing backend command or scope types to stable submission code.
/// </summary>
public interface IIndirectDrawStateBackendCapability
{
    bool TryBeginIndirectDrawState(
        XRRenderProgram program,
        XRMaterial? material,
        in Matrix4x4 modelMatrix,
        out IndirectDrawStateToken token);

    void EndIndirectDrawState(in IndirectDrawStateToken token);
}

/// <summary>Stable allocation-free restoration token for indirect draw state.</summary>
public readonly record struct IndirectDrawStateToken(
    XRRenderProgram? PreviousProgram,
    XRMaterial? PreviousMaterial,
    Matrix4x4 PreviousModelMatrix,
    bool HadPreviousState);

/// <summary>Allocation-free scope used by stable indirect submission code.</summary>
public readonly struct IndirectDrawStateCapabilityScope(
    IIndirectDrawStateBackendCapability? capability,
    IndirectDrawStateToken token) : IDisposable
{
    public void Dispose()
        => capability?.EndIndirectDrawState(token);
}
