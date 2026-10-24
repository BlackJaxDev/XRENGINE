using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>
/// Supplies immutable managed data for one accepted pointer size. The hit packet
/// keeps the value alive. The value must not own native or disposable resources.
/// Return null when the source, build, or accepted size is not coherent.
/// Invalidate HitTest when the metadata changes.
/// </summary>
public interface IUIPointerMetadataSource
{
    /// <summary>Gets the frozen metadata for the accepted control size.</summary>
    object? CapturePointerMetadata(Vector2 acceptedSize);
}
