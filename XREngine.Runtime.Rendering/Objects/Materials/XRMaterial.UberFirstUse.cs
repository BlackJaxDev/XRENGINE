using System.Threading;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    private long _uberVariantDrawRequestUberRevision;
    private long _uberVariantDrawRequestShaderRevision;
    private int _uberVariantFailureReported;

    /// <summary>
    /// Starts the asynchronous Uber variant build when a backend draws this material and the
    /// material state changed since the last such request. A backend calls this from its per-draw
    /// program lookup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The method records <see cref="UberStateRevision"/> and <see cref="ShaderStateRevision"/>
    /// at each request. While both stay the same, a call reads two values and returns, so the
    /// check costs no allocation per frame. When either changes (an authored feature or property
    /// edit clears the active variant and advances the Uber revision; a shader swap advances the
    /// shader revision), the next draw calls <see cref="RequestUberVariantPreparationIfNeeded"/>
    /// again. That call returns at once when the current variant is still renderable, and it
    /// starts a worker-task build when the variant is missing or stale.
    /// </para>
    /// <para>
    /// Until the variant is ready, the material keeps the canonical Uber fragment shader. That
    /// shader turns off the optional modules (emission, rim lighting, alpha masks and others), so
    /// an unprepared material renders as a plain lit surface. The adoption of a variant changes
    /// the shader list and <see cref="ActiveUberVariant"/>, so the backend builds a new program on
    /// a later draw. After a failed build, the request runs again only when a revision changes, so
    /// a failing material does not start a new build on every draw. Non-Uber materials return
    /// without work.
    /// </para>
    /// </remarks>
    public void RequestUberVariantPreparationForDraw()
    {
        long uberRevision = UberStateRevision;
        long shaderRevision = ShaderStateRevision;
        if (Volatile.Read(ref _uberVariantDrawRequestUberRevision) == uberRevision &&
            Volatile.Read(ref _uberVariantDrawRequestShaderRevision) == shaderRevision)
            return;

        Volatile.Write(ref _uberVariantDrawRequestUberRevision, uberRevision);
        Volatile.Write(ref _uberVariantDrawRequestShaderRevision, shaderRevision);
        RequestUberVariantPreparationIfNeeded();
    }

    /// <summary>
    /// Writes one rendering warning the first time a variant build of this material fails. The
    /// material then keeps rendering with the canonical Uber fragment shader, which has no
    /// emission, rim lighting or alpha masks, so the warning names the material and the failure
    /// reason to make the visual difference traceable.
    /// </summary>
    private void ReportUberVariantFailureOnce(UberMaterialVariantStatus status)
    {
        if (Interlocked.Exchange(ref _uberVariantFailureReported, 1) != 0)
            return;

        Debug.RenderingWarning(
            "[UberVariant] Variant build failed for material '{0}'. It renders with the canonical Uber fragment shader (optional modules off). Requested variant={1:X16}. Reason={2}",
            Name ?? "<unnamed material>",
            status.RequestedVariantHash,
            status.FailureReason ?? "No failure reason was recorded.");
    }
}
