using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Publishes typed numeric shader values with an explicit owner frequency and
/// monotonic content generation.
/// </summary>
/// <remarks>
/// Implementations must advance <see cref="Generation"/> whenever any emitted
/// value changes. Fast-path publishers must use typed <c>Uniform</c> overloads
/// only; descriptor resources remain on the explicit legacy path until they
/// have a frequency-owned descriptor publication contract.
/// </remarks>
public interface IRenderBindingPublisher
{
    /// <summary>
    /// Gets the owner domain whose generation controls the published values.
    /// </summary>
    ERenderBindingFrequency Frequency { get; }

    /// <summary>
    /// Gets a non-zero monotonic generation for the current published content.
    /// </summary>
    ulong Generation { get; }

    /// <summary>
    /// Publishes typed numeric values into the backend's private capture.
    /// </summary>
    void PublishUniforms(
        XRRenderProgram vertexProgram,
        XRRenderProgram materialProgram);
}
