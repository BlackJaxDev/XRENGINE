using System.Numerics;

namespace XREngine.Rendering.Models.Gaussian.Baking.Views;

/// <summary>
/// Gives one perspective camera that a bake renders from. The camera looks along its local -Z axis,
/// and its local +Y axis is up, as in the engine camera transform.
/// </summary>
/// <param name="Position">The camera position in world space.</param>
/// <param name="Rotation">The camera rotation in world space.</param>
/// <param name="VerticalFovDegrees">The vertical field of view, in degrees.</param>
public readonly record struct BakeCameraPose(Vector3 Position, Quaternion Rotation, float VerticalFovDegrees);
