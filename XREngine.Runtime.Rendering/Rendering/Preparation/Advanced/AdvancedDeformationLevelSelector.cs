using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Chooses the minimum cluster level of a far skinned draw for deformation level of detail.
/// The cook stores a coarse-first deformation order and, for each level, the largest finite
/// group error of that level (<c>MeshletPayload.LevelMaxErrors</c>). When the skinned cluster
/// cut would treat every group finer than level <c>L</c> as refined enough, the draw never shows
/// a cluster finer than <c>L</c>, so its deformation job deforms only the prefix of the order for
/// <c>L</c> and the cut is clamped to <c>L</c>.
/// </summary>
/// <remarks>
/// The GPU cut of a skinned draw measures one distance per view pair: the nearer eye to the
/// nearer of the instance sphere and the visibility candidate sphere, less the blendshape margin
/// times the draw scale, not below the near distance. The projected error of a group is
/// <c>error * maxScale * pixelScale / distance</c>. This selector uses a distance that is never
/// larger (all eyes of the view set, and a physics chain proxy box that contains the candidate
/// sphere of a GPU physics draw) and a pixel scale that is never smaller (the largest of all views,
/// times <see cref="SafetyFactor"/>). The chosen level therefore never clamps a group that the
/// unclamped cut of any view draws. The clamp in the shader keeps the draw correct when a view
/// renders at a larger internal size than its output rectangle.
/// </remarks>
public static class AdvancedDeformationLevelSelector
{
    /// <summary>
    /// Factor on the projected error for small differences between the CPU and GPU inputs
    /// (float rounding, posed bounds that change between extraction and the cut).
    /// </summary>
    public const float SafetyFactor = 1.25f;

    /// <summary>
    /// Reads the eye positions, the largest pixel scale, and the smallest near distance of a
    /// view set. Returns false when a view is orthographic or has no usable projection or size;
    /// the caller then keeps every draw at level 0.
    /// </summary>
    /// <param name="views">The views that the frame's visibility pass renders.</param>
    /// <param name="eyes">Receives one world eye position per view.</param>
    /// <param name="eyeCount">The number of eye positions written.</param>
    /// <param name="pixelScale">
    /// Pixels for one world unit of error at distance 1: the larger projection scale term times
    /// half the view height, the largest value of all views.
    /// </param>
    /// <param name="nearDistance">The smallest near distance of all views, at least 1e-4.</param>
    public static bool TryCaptureViews(
        in RenderFrameViewSet views,
        Span<Vector3> eyes,
        out int eyeCount,
        out float pixelScale,
        out float nearDistance)
    {
        eyeCount = 0;
        pixelScale = 0.0f;
        nearDistance = float.MaxValue;
        if (views.ViewCount <= 0 || views.ViewCount > eyes.Length)
            return false;

        for (int index = 0; index < views.ViewCount; index++)
        {
            RenderFrameViewDescriptor view = views.GetView(index);
            Matrix4x4 projection = view.ProjectionMatrixUnjittered == default
                ? view.ProjectionMatrix
                : view.ProjectionMatrixUnjittered;
            // A perspective projection has M44 == 0. The error model divides by distance, which
            // does not apply to an orthographic view.
            if (projection.M44 != 0.0f || view.ViewRect.Height == 0u)
                return false;
            float scale = MathF.Max(MathF.Abs(projection.M11), MathF.Abs(projection.M22));
            float viewPixelScale = scale * view.ViewRect.Height * 0.5f;
            if (!float.IsFinite(viewPixelScale) || viewPixelScale <= 0.0f)
                return false;

            Vector3 eye = new(view.CameraPositionAndNear.X, view.CameraPositionAndNear.Y, view.CameraPositionAndNear.Z);
            if (view.CameraPositionAndNear == Vector4.Zero)
            {
                if (!Matrix4x4.Invert(view.ViewMatrix, out Matrix4x4 cameraWorld))
                    return false;
                eye = cameraWorld.Translation;
            }
            if (!float.IsFinite(eye.X) || !float.IsFinite(eye.Y) || !float.IsFinite(eye.Z))
                return false;

            eyes[eyeCount++] = eye;
            pixelScale = MathF.Max(pixelScale, viewPixelScale);
            nearDistance = MathF.Min(nearDistance, MathF.Max(view.CameraPositionAndNear.W, 1.0e-4f));
        }
        return true;
    }

    /// <summary>
    /// The largest axis scale of a world matrix, as the visibility task stage computes it
    /// (<c>XR_ADV_MeshletDrawCullState</c>): the lengths of the first three matrix columns.
    /// </summary>
    public static float MaxAxisScale(in Matrix4x4 world)
    {
        float scaleX = new Vector3(world.M11, world.M21, world.M31).Length();
        float scaleY = new Vector3(world.M12, world.M22, world.M32).Length();
        float scaleZ = new Vector3(world.M13, world.M23, world.M33).Length();
        return MathF.Max(scaleX, MathF.Max(scaleY, scaleZ));
    }

    /// <summary>
    /// Computes the distance that the level choice uses for one skinned draw. It is never larger
    /// than the shared distance of the GPU cut for any view of the set. Returns a negative value
    /// when the bounds are not usable; the caller then keeps the draw at level 0.
    /// </summary>
    /// <param name="eyes">The eye positions of the view set.</param>
    /// <param name="boundsSphere">The posed world sphere of the instance record.</param>
    /// <param name="hasPhysicsBounds">
    /// True for a draw whose candidate sphere the GPU physics chain bounds patch rewrites.
    /// </param>
    /// <param name="physicsMin">Minimum of the committed CPU proxy box of the physics bounds.</param>
    /// <param name="physicsMax">Maximum of the committed CPU proxy box of the physics bounds.</param>
    /// <param name="margin">The blendshape margin of the draw in mesh units.</param>
    /// <param name="maxScale">The largest axis scale of the draw's world matrix.</param>
    /// <param name="nearDistance">The smallest near distance of the view set.</param>
    public static float DrawDistance(
        ReadOnlySpan<Vector3> eyes,
        Vector4 boundsSphere,
        bool hasPhysicsBounds,
        Vector3 physicsMin,
        Vector3 physicsMax,
        float margin,
        float maxScale,
        float nearDistance)
    {
        if (eyes.IsEmpty || !IsFinite(boundsSphere) || boundsSphere.W < 0.0f ||
            !float.IsFinite(margin) || margin < 0.0f || !float.IsFinite(maxScale))
            return -1.0f;

        Vector3 center = new(boundsSphere.X, boundsSphere.Y, boundsSphere.Z);
        float nearest = float.MaxValue;
        for (int index = 0; index < eyes.Length; index++)
            nearest = MathF.Min(nearest, Vector3.Distance(eyes[index], center) - boundsSphere.W);

        if (hasPhysicsBounds)
        {
            if (!IsFinite(physicsMin) || !IsFinite(physicsMax) ||
                physicsMax.X < physicsMin.X || physicsMax.Y < physicsMin.Y || physicsMax.Z < physicsMin.Z)
                return -1.0f;
            // The patched candidate sphere has its center in the exact physics box, which the
            // proxy box contains, and its radius is at most the half diagonal of the proxy box.
            float halfDiagonal = Vector3.Distance(physicsMin, physicsMax) * 0.5f;
            for (int index = 0; index < eyes.Length; index++)
            {
                Vector3 eye = eyes[index];
                Vector3 closest = Vector3.Clamp(eye, physicsMin, physicsMax);
                nearest = MathF.Min(nearest, Vector3.Distance(eye, closest) - halfDiagonal);
            }
        }

        float distance = MathF.Max(nearest, 0.0f) - margin * maxScale;
        return float.IsFinite(distance) ? MathF.Max(distance, nearDistance) : -1.0f;
    }

    /// <summary>
    /// Chooses the largest level <c>L</c> for which every group finer than <c>L</c> projects to
    /// at most <paramref name="pixelThreshold"/> pixels at <paramref name="distance"/>. The result
    /// is at most the coarsest level (<c>levelMaxErrors.Length - 1</c>).
    /// </summary>
    /// <param name="levelMaxErrors">The largest finite group error of each level, in mesh units.</param>
    /// <param name="distance">The distance from <see cref="DrawDistance"/>.</param>
    /// <param name="maxScale">The largest axis scale of the draw's world matrix.</param>
    /// <param name="pixelScale">The largest pixel scale of the view set.</param>
    /// <param name="pixelThreshold">The cluster cut threshold in render pixels.</param>
    public static uint ChooseLevel(
        ReadOnlySpan<float> levelMaxErrors,
        float distance,
        float maxScale,
        float pixelScale,
        float pixelThreshold)
    {
        if (levelMaxErrors.Length < 2 || !(distance > 0.0f))
            return 0u;
        float pixelsPerError = maxScale * pixelScale * SafetyFactor / distance;
        if (!float.IsFinite(pixelsPerError) || pixelsPerError <= 0.0f)
            return 0u;

        float worstError = 0.0f;
        uint level = 0u;
        for (int finer = 0; finer + 1 < levelMaxErrors.Length; finer++)
        {
            worstError = MathF.Max(worstError, levelMaxErrors[finer]);
            if (worstError * pixelsPerError > pixelThreshold)
                break;
            level = (uint)finer + 1u;
        }
        return level;
    }

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
