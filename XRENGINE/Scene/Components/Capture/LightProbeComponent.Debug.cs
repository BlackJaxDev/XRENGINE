using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Capture.Lights
{
    public partial class LightProbeComponent
    {
        #region Debug Rendering Methods

        private void RenderCameraOrientationDebug()
        {
            using var prof = Engine.Profiler.Start("LightProbeComponent.RenderCameraOrientationDebug");

            const float forwardOffset = 0.7f;
            const float frameHalfExtent = 0.18f;
            const float frameInset = 0.02f;
            const float labelLift = 0.25f;
            const float axisLength = 0.32f;
            const float axisOffset = 0.08f;
            const float arrowSize = 0.08f;

            for (int i = 0; i < Viewports.Length; ++i)
            {
                var viewport = Viewports[i];
                var transform = viewport?.Camera?.Transform;
                if (transform is null)
                    continue;

                Vector3 cameraOrigin = transform.RenderTranslation + transform.RenderForward * forwardOffset;
                FaceDebugInfo faceInfo = i < s_faceDebugInfos.Length ? s_faceDebugInfos[i] : s_faceDebugInfos[^1];

                RenderFaceFrame(cameraOrigin, transform, frameHalfExtent, frameInset, faceInfo.Color);
                Engine.Rendering.Debug.RenderText(
                    cameraOrigin + transform.RenderUp * labelLift,
                    $"{faceInfo.Name} face",
                    faceInfo.Color);

                RenderAxisPair(cameraOrigin, transform.RenderRight, ColorF4.Red, "+X", "-X", axisLength, axisOffset, arrowSize);
                RenderAxisPair(cameraOrigin, transform.RenderUp, ColorF4.Green, "+Y", "-Y", axisLength, axisOffset, arrowSize);
                RenderAxisPair(cameraOrigin, transform.RenderForward, ColorF4.Blue, "-Z", "+Z", axisLength, axisOffset, arrowSize);
            }
        }

        private void RenderVolumesDebug()
        {
            using var prof = Engine.Profiler.Start("LightProbeComponent.RenderVolumesDebug");

            Vector3 probeOrigin = Transform.RenderTranslation;

            // Influence volume visualization
            Vector3 influenceCenter = probeOrigin + InfluenceOffset;
            const float alpha = 0.35f;
            ColorF4 outerColor = new(0.35f, 0.75f, 1.0f, alpha);
            ColorF4 innerColor = new(0.65f, 1.0f, 0.85f, alpha * 0.9f);

            if (InfluenceShape == EInfluenceShape.Sphere)
            {
                Engine.Rendering.Debug.RenderSphere(influenceCenter, InfluenceSphereOuterRadius, false, outerColor);
                if (InfluenceSphereInnerRadius > 0.0001f)
                    Engine.Rendering.Debug.RenderSphere(influenceCenter, InfluenceSphereInnerRadius, false, innerColor);
            }
            else
            {
                Matrix4x4 influenceTransform = Matrix4x4.Identity;
                Engine.Rendering.Debug.RenderBox(InfluenceBoxOuterExtents, influenceCenter, influenceTransform, false, outerColor);
                Engine.Rendering.Debug.RenderBox(InfluenceBoxInnerExtents, influenceCenter, influenceTransform, false, innerColor);
            }

            // Proxy box visualization (parallax volume)
            ColorF4 proxyColor = new(1.0f, 0.6f, 0.2f, alpha);
            Matrix4x4 proxyRotation = Matrix4x4.CreateFromQuaternion(ProxyBoxRotation);
            Vector3 proxyCenter = probeOrigin + ProxyBoxCenterOffset;
            Engine.Rendering.Debug.RenderBox(ProxyBoxHalfExtents, proxyCenter, proxyRotation, false, proxyColor);
        }

        private static void RenderAxisPair(
            Vector3 origin,
            Vector3 direction,
            ColorF4 color,
            string positiveLabel,
            string negativeLabel,
            float length,
            float offset,
            float arrowSize)
        {
            if (direction.LengthSquared() <= float.Epsilon)
                return;

            Vector3 normalized = Vector3.Normalize(direction);
            Vector3 positiveStart = origin + normalized * offset;
            Vector3 positiveEnd = origin + normalized * (offset + length);
            RenderArrow(positiveStart, positiveEnd, normalized, color, arrowSize);
            Engine.Rendering.Debug.RenderText(positiveEnd + normalized * (offset * 0.5f), positiveLabel, color);

            Vector3 negDir = -normalized;
            Vector3 negativeStart = origin + negDir * offset;
            Vector3 negativeEnd = origin + negDir * (offset + length);
            var negativeColor = color * 0.7f;
            RenderArrow(negativeStart, negativeEnd, negDir, negativeColor, arrowSize * 0.75f);
            Engine.Rendering.Debug.RenderText(negativeEnd + negDir * (offset * 0.5f), negativeLabel, negativeColor);
        }

        private static void RenderArrow(Vector3 start, Vector3 end, Vector3 direction, ColorF4 color, float arrowSize)
        {
            Engine.Rendering.Debug.RenderLine(start, end, color);

            Vector3 dirNorm = Vector3.Normalize(direction);
            Vector3 ortho = Math.Abs(Vector3.Dot(dirNorm, Vector3.UnitY)) > 0.9f
                ? Vector3.Normalize(Vector3.Cross(dirNorm, Vector3.UnitX))
                : Vector3.Normalize(Vector3.Cross(dirNorm, Vector3.UnitY));
            Vector3 ortho2 = Vector3.Normalize(Vector3.Cross(dirNorm, ortho));

            Vector3 headBase = end - dirNorm * arrowSize;
            Vector3 wingA = headBase + ortho * arrowSize * 0.5f;
            Vector3 wingB = headBase - ortho * arrowSize * 0.5f;
            Vector3 wingC = headBase + ortho2 * arrowSize * 0.5f;
            Vector3 wingD = headBase - ortho2 * arrowSize * 0.5f;

            Engine.Rendering.Debug.RenderLine(end, wingA, color);
            Engine.Rendering.Debug.RenderLine(end, wingB, color);
            Engine.Rendering.Debug.RenderLine(end, wingC, color);
            Engine.Rendering.Debug.RenderLine(end, wingD, color);
        }

        private static void RenderFaceFrame(Vector3 origin, TransformBase transform, float halfExtent, float inset, ColorF4 color)
        {
            Vector3 right = Vector3.Normalize(transform.RenderRight);
            Vector3 up = Vector3.Normalize(transform.RenderUp);
            Vector3 forward = Vector3.Normalize(transform.RenderForward);

            Vector3 topLeft = origin + (-right * halfExtent) + (up * halfExtent);
            Vector3 topRight = origin + (right * halfExtent) + (up * halfExtent);
            Vector3 bottomLeft = origin + (-right * halfExtent) - (up * halfExtent);
            Vector3 bottomRight = origin + (right * halfExtent) - (up * halfExtent);

            Engine.Rendering.Debug.RenderLine(topLeft, topRight, color);
            Engine.Rendering.Debug.RenderLine(topRight, bottomRight, color);
            Engine.Rendering.Debug.RenderLine(bottomRight, bottomLeft, color);
            Engine.Rendering.Debug.RenderLine(bottomLeft, topLeft, color);

            Vector3 crossHorizontalStart = origin + (-right * (halfExtent - inset));
            Vector3 crossHorizontalEnd = origin + (right * (halfExtent - inset));
            Vector3 crossVerticalStart = origin + (-up * (halfExtent - inset));
            Vector3 crossVerticalEnd = origin + (up * (halfExtent - inset));

            Engine.Rendering.Debug.RenderLine(crossHorizontalStart, crossHorizontalEnd, color * 0.9f);
            Engine.Rendering.Debug.RenderLine(crossVerticalStart, crossVerticalEnd, color * 0.9f);

            Engine.Rendering.Debug.RenderLine(origin, origin + forward * inset, color);
        }

        #endregion
    }
}
