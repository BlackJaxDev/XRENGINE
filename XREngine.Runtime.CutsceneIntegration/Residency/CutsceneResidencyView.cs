using System.Numerics;
using XREngine.Components;
using XREngine.Rendering;

namespace XREngine.Cutscenes;

/// <summary>
/// The view of one camera at one time, in world space. The manifest bake and the approximate manifest use it to
/// estimate the screen coverage of a bounding sphere.
/// </summary>
/// <param name="Position">The camera position.</param>
/// <param name="Forward">The unit view direction.</param>
/// <param name="Up">The unit up direction. It must not be parallel to <paramref name="Forward"/>.</param>
/// <param name="Orthographic">True for an orthographic camera.</param>
/// <param name="VerticalFieldOfViewRadians">The vertical field of view of a perspective camera, in radians.</param>
/// <param name="OrthographicHeight">The view height of an orthographic camera, in world units.</param>
/// <param name="AspectRatio">The width divided by the height. It must be greater than zero.</param>
/// <param name="NearPlane">The distance of the near plane. It must be greater than zero.</param>
public readonly record struct CutsceneResidencyView(
    Vector3 Position,
    Vector3 Forward,
    Vector3 Up,
    bool Orthographic,
    float VerticalFieldOfViewRadians,
    float OrthographicHeight,
    float AspectRatio,
    float NearPlane)
{
    /// <summary>The aspect ratio of a camera that takes its aspect ratio from a viewport that does not exist yet: 16:9.</summary>
    public const float DefaultAspectRatio = 16.0f / 9.0f;

    /// <summary>
    /// Creates the view of a camera component from the current world matrix of its camera transform.
    /// Returns false if the camera has a projection that is not perspective or orthographic.
    /// </summary>
    /// <param name="camera">The camera component.</param>
    /// <param name="view">The view.</param>
    public static bool TryCreate(CameraComponent camera, out CutsceneResidencyView view)
    {
        ArgumentNullException.ThrowIfNull(camera);

        XRCamera xrCamera = camera.Camera;
        Matrix4x4 world = xrCamera.Transform.WorldMatrix;
        Vector3 position = world.Translation;
        Vector3 forward = SafeNormalize(Vector3.TransformNormal(Globals.Forward, world), Globals.Forward);
        Vector3 up = SafeNormalize(Vector3.TransformNormal(Globals.Up, world), Globals.Up);

        switch (xrCamera.Parameters)
        {
            case XRPerspectiveCameraParameters perspective:
                view = new CutsceneResidencyView(
                    position, forward, up, false,
                    perspective.VerticalFieldOfView * (MathF.PI / 180.0f), 0.0f,
                    ValidAspect(perspective.AspectRatio), MathF.Max(perspective.NearZ, 1e-4f));
                return true;
            case XROrthographicCameraParameters orthographic:
                view = new CutsceneResidencyView(
                    position, forward, up, true,
                    0.0f, MathF.Max(orthographic.Height, 1e-4f),
                    ValidAspect(orthographic.AspectRatio), MathF.Max(orthographic.NearZ, 1e-4f));
                return true;
            default:
                view = default;
                return false;
        }
    }

    /// <summary>
    /// Estimates the fraction of the screen, from 0 to 1, that a bounding sphere covers. The estimate is the part of the
    /// projected box of the sphere that is inside the screen, multiplied by pi/4 for the circle. A sphere behind the
    /// near plane covers nothing, so the orientation of the camera counts. A sphere that holds the camera covers all of it.
    /// </summary>
    /// <param name="center">The sphere center in world space.</param>
    /// <param name="radius">The sphere radius in world space.</param>
    public float ScreenCoverage(Vector3 center, float radius)
    {
        if (!(radius > 0.0f))
            return 0.0f;

        Vector3 toCenter = center - Position;
        if (toCenter.LengthSquared() <= radius * radius)
            return 1.0f;

        float depth = Vector3.Dot(toCenter, Forward);
        if (depth + radius <= NearPlane)
            return 0.0f;

        Vector3 right = SafeNormalize(Vector3.Cross(Forward, Up), Vector3.UnitX);
        Vector3 up = Vector3.Cross(right, Forward);
        float x = Vector3.Dot(toCenter, right);
        float y = Vector3.Dot(toCenter, up);

        float halfHeight;
        if (Orthographic)
            halfHeight = OrthographicHeight * 0.5f;
        else
            halfHeight = MathF.Max(depth, NearPlane) * MathF.Tan(VerticalFieldOfViewRadians * 0.5f);
        if (!(halfHeight > 0.0f))
            return 0.0f;

        float halfWidth = halfHeight * AspectRatio;
        float overlapX = Overlap(x / halfWidth, radius / halfWidth);
        float overlapY = Overlap(y / halfHeight, radius / halfHeight);

        // The screen is 2 x 2 in normalized units. A circle fills pi/4 of its box.
        float coverage = overlapX * overlapY * 0.25f * (MathF.PI * 0.25f);
        return Math.Clamp(coverage, 0.0f, 1.0f);
    }

    private static float Overlap(float center, float halfSize)
        => MathF.Max(0.0f, MathF.Min(center + halfSize, 1.0f) - MathF.Max(center - halfSize, -1.0f));

    private static float ValidAspect(float aspect)
        => float.IsFinite(aspect) && aspect > 0.0f ? aspect : DefaultAspectRatio;

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        float length = value.Length();
        return length > 1e-6f && float.IsFinite(length) ? value / length : fallback;
    }
}
