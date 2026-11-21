namespace XREngine.Rendering;

/// <summary>
/// A scene component whose GPU data can be incomplete for some frames after a change, for example a splat set that is
/// still sampling or uploading. A frame recorder reads <see cref="IsReadyForCapture"/> on every component of the recorded
/// world that implements this interface, and captures a frame only when all of them are ready. A normal render does not
/// read it.
/// </summary>
public interface IRenderCaptureReadiness
{
    /// <summary>True when the last rendered frame showed the complete current state of the component.</summary>
    bool IsReadyForCapture { get; }

    /// <summary>A short text for a recorder timeout message that tells which part of the component is not ready.</summary>
    string DescribeCaptureReadiness();
}
