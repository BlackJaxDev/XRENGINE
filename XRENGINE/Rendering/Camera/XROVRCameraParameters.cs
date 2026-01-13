using Extensions;
using System;
using System.Numerics;
using Valve.VR;
using XREngine.Data.Geometry;

namespace XREngine.Rendering
{
    /// <summary>
    /// Retrieves the view and projection matrices for a VR eye camera from OpenVR.
    /// </summary>
    /// <param name="leftEye"></param>
    /// <param name="nearPlane"></param>
    /// <param name="farPlane"></param>
    public class XROVRCameraParameters(bool leftEye, float nearPlane, float farPlane) 
        : XRCameraParameters(nearPlane, farPlane)
    {
        private bool _leftEye = leftEye;
        public bool LeftEye
        {
            get => _leftEye;
            set => SetField(ref _leftEye, value);
        }

        public override Vector2 GetFrustumSizeAtDistance(float drawDistance)
        {
            var invProj = GetProjectionMatrix().Inverted();
            float normDist = (drawDistance - NearZ) / (FarZ - NearZ);
            //unproject the the points on the clip space box at normalized distance
            Vector3 bottomLeft = Vector3.Transform(new Vector3(-1, -1, normDist), invProj);
            Vector3 bottomRight = Vector3.Transform(new Vector3(1, -1, normDist), invProj);
            Vector3 topLeft = Vector3.Transform(new Vector3(-1, 1, normDist), invProj);
            //calculate the size of the frustum at the given distance
            return new Vector2((bottomRight - bottomLeft).Length(), (topLeft - bottomLeft).Length());
        }
        
        protected override Matrix4x4 CalculateProjectionMatrix()
        {
            // OpenXR: use per-eye asymmetric FOV from xrLocateViews (stored in OpenXRAPI).
            if (Engine.VRState.IsOpenXRActive)
            {
                var oxr = Engine.VRState.OpenXRApi;
                if (oxr is not null &&
                    oxr.TryGetEyeFovAngles(LeftEye, out float angleLeft, out float angleRight, out float angleUp, out float angleDown))
                {
                    float n = NearZ;
                    if (n <= float.Epsilon)
                        n = 0.001f;

                    float l = n * MathF.Tan(angleLeft);
                    float r = n * MathF.Tan(angleRight);
                    float b = n * MathF.Tan(angleDown);
                    float t = n * MathF.Tan(angleUp);

                    return Matrix4x4.CreatePerspectiveOffCenter(l, r, b, t, n, FarZ);
                }
            }

            var api = Engine.VRState.OpenVRApi;
            if (!api.IsHeadsetPresent || api.CVR is null)
                return Matrix4x4.CreatePerspectiveFieldOfView(float.DegreesToRadians(90.0f), 1.0f, NearZ, FarZ);

            EVREye eye = LeftEye ? EVREye.Eye_Left : EVREye.Eye_Right;

            //float left = 0.0f, right = 0.0f, top = 0.0f, bottom = 0.0f;
            //api.CVR.GetProjectionRaw(eye, ref left, ref right, ref top, ref bottom);

            ////See https://github.com/ValveSoftware/openvr/wiki/IVRSystem::GetProjectionRaw
            //left *= NearZ;
            //right *= NearZ;
            //top *= NearZ;
            //bottom *= NearZ;

            //Debug.Out($"Projection matrix for {eye}: [l:{left}, r:{right}, t:{top}, b:{bottom}]");

            //Top and bottom are swapped
            //return Matrix4x4.CreatePerspectiveOffCenter(left, right, top, bottom, NearZ, FarZ);

            return api.CVR.GetProjectionMatrix(eye, NearZ, FarZ).ToNumerics().Transposed();
        }

        protected override Frustum CalculateUntransformedFrustum()
            => new((_projectionMatrix ?? CalculateProjectionMatrix()).Inverted());
    }
}
