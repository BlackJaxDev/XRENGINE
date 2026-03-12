using System.Numerics;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// Describes parent-managed child placement state without depending on a specific host UI layer.
    /// </summary>
    public interface ITransformChildPlacementInfo
    {
        bool RelativePositioningChanged { get; set; }
        Matrix4x4 GetRelativeItemMatrix();
    }
}
