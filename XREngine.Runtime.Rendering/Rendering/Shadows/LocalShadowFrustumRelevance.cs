using System.Numerics;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Data.Geometry;
using XREngine.Rendering;

namespace XREngine.Rendering.Shadows;

internal readonly struct ShadowRelevanceCameraSet(IReadOnlyList<PreparedFrustum> frusta)
{
    private readonly IReadOnlyList<PreparedFrustum> _frusta = frusta;

    public int Count => _frusta?.Count ?? 0;

    public PreparedFrustum this[int index] => _frusta[index];
}

internal static class LocalShadowFrustumRelevance
{
    public const int AllPointFacesMask = (1 << PointLightComponent.ShadowFaceCount) - 1;

    public static bool IsPointFaceRelevant(
        PointLightComponent light,
        int faceIndex,
        in ShadowRelevanceCameraSet cameras,
        List<Vector3> intersectionScratch)
    {
        if (light is null || (uint)faceIndex >= (uint)PointLightComponent.ShadowFaceCount)
            return true;

        if (cameras.Count <= 0)
            return true;

        if (!light.TryGetShadowFaceCamera(faceIndex, out XRCamera faceCamera))
            return true;

        try
        {
            PreparedFrustum faceFrustum = faceCamera.WorldFrustum().Prepare();
            return AnyCameraIntersectsShadowFrustum(faceFrustum, cameras, intersectionScratch);
        }
        catch
        {
            return true;
        }
    }

    public static bool IsSpotShadowRelevant(
        SpotLightComponent light,
        in ShadowRelevanceCameraSet cameras,
        List<Vector3> intersectionScratch)
    {
        if (light is null || cameras.Count <= 0)
            return true;

        XRCamera? shadowCamera = light.ShadowCamera;
        if (shadowCamera is null)
            return true;

        try
        {
            PreparedFrustum spotFrustum = shadowCamera.WorldFrustum().Prepare();
            return AnyCameraIntersectsShadowFrustum(spotFrustum, cameras, intersectionScratch);
        }
        catch
        {
            return true;
        }
    }

    public static bool AnyCameraIntersectsShadowFrustum(
        PreparedFrustum? shadowFrustum,
        in ShadowRelevanceCameraSet cameras,
        List<Vector3> intersectionScratch)
    {
        if (!IsUsableFrustum(shadowFrustum) || cameras.Count <= 0)
            return true;

        if (intersectionScratch is null)
            return true;

        try
        {
            for (int i = 0; i < cameras.Count; i++)
            {
                PreparedFrustum cameraFrustum = cameras[i];
                if (!IsUsableFrustum(cameraFrustum))
                    return true;

                if (PreparedFrustum.FrustumIntersection.TryIntersectFrustaPoints(
                    shadowFrustum!,
                    cameraFrustum,
                    intersectionScratch))
                {
                    return true;
                }
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    private static bool IsUsableFrustum(PreparedFrustum? frustum)
    {
        if (frustum is null ||
            frustum.PlaneCount <= 0 ||
            frustum.Planes is null ||
            frustum.Corners is null ||
            frustum.Planes.Length < frustum.PlaneCount ||
            frustum.Corners.Length < 8 ||
            !float.IsFinite(frustum.SphereRadius))
        {
            return false;
        }

        for (int i = 0; i < frustum.PlaneCount; i++)
        {
            Plane plane = frustum.Planes[i];
            if (!float.IsFinite(plane.Normal.X) ||
                !float.IsFinite(plane.Normal.Y) ||
                !float.IsFinite(plane.Normal.Z) ||
                !float.IsFinite(plane.D))
            {
                return false;
            }
        }

        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = frustum.Corners[i];
            if (!float.IsFinite(corner.X) ||
                !float.IsFinite(corner.Y) ||
                !float.IsFinite(corner.Z))
            {
                return false;
            }
        }

        return true;
    }
}
