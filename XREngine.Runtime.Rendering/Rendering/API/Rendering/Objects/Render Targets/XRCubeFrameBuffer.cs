using System.Drawing.Drawing2D;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Transforms.Rotations;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering
{
    public class XRCubeFrameBuffer : XRMaterialFrameBuffer
    {
        public event DelSetUniforms? SettingUniforms;

        public XRMeshRenderer FullScreenCubeMesh { get; }

        /// <summary>
        /// These cameras are used to render each face of the clip-space cube.
        /// </summary>
        private static readonly XRCamera[] LocalCameras = GetCamerasPerFace(0.1f, 1.0f, false, null);

        public XRCubeFrameBuffer(XRMaterial? mat) : base(mat)
        {
            //if (mat is not null)
            //    mat.RenderOptions.CullMode = ECullMode.None;
            FullScreenCubeMesh = new XRMeshRenderer(XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f), true), mat);
            FullScreenCubeMesh.GenerationPriority = EMeshGenerationPriority.RenderPipeline;
            FullScreenCubeMesh.EnsureRenderPipelineVersionsCreated();
            FullScreenCubeMesh.SettingUniforms += SetUniforms;
        }

        private void SetUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
            => SettingUniforms?.Invoke(materialProgram);

        public bool TryPrepareForRendering(bool forceNoStereo = true)
            => FullScreenCubeMesh.TryPrepareForRendering(forceNoStereo);

        /// <summary>
        /// Renders the one side of the FBO to the entire region set by Engine.Rendering.State.PushRenderArea.
        /// </summary>
        public bool RenderFullscreen(ECubemapFace face)
        {
            if (!TryPrepareForRendering(true))
                return false;

            var cam = LocalCameras[(int)face];

            var state = Engine.Rendering.State.RenderingPipelineState;
            if (state is not null)
            {
                using (state.PushRenderingCamera(cam))
                    FullScreenCubeMesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, 1, true);
            }
            else
            {
                Engine.Rendering.State.RenderingCameraOverride = cam;
                FullScreenCubeMesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, 1, true);
                Engine.Rendering.State.RenderingCameraOverride = null;
            }

            return true;
        }

        /// <summary>
        /// Helper function to create cameras for each face of a cube.
        /// </summary>
        /// <param name="nearZ"></param>
        /// <param name="farZ"></param>
        /// <param name="perspective"></param>
        /// <param name="parent"></param>
        public static XRCamera[] GetCamerasPerFace(float nearZ, float farZ, bool perspective, TransformBase? parent)
        {
            XRCamera[] cameras = new XRCamera[6];
            (Vector3 Forward, Vector3 Up)[] faces =
            [
                ( Vector3.UnitX, -Vector3.UnitY), // +X
                (-Vector3.UnitX, -Vector3.UnitY), // -X
                ( Vector3.UnitY,  Vector3.UnitZ), // +Y
                (-Vector3.UnitY, -Vector3.UnitZ), // -Y
                ( Vector3.UnitZ, -Vector3.UnitY), // +Z
                (-Vector3.UnitZ, -Vector3.UnitY), // -Z
            ];

            XRCameraParameters p;
            if (perspective)
                p = new XRPerspectiveCameraParameters(90.0f, 1.0f, nearZ, farZ);
            else
            {
                var ortho = new XROrthographicCameraParameters(1.0f, 1.0f, nearZ, farZ);
                ortho.SetOriginPercentages(0.5f, 0.5f);
                p = ortho;
            }

            for (int i = 0; i < 6; ++i)
            {
                var tfm = new Transform()
                {
                    // XRMath.LookRotation aligns the local +Z axis, while Transform.Forward is -Z.
                    // Negate the desired face forward so the resulting transform's WorldForward
                    // matches the cubemap face direction used by OpenGL sampling.
                    Rotation = XRMath.LookRotation(-faces[i].Forward, faces[i].Up),
                    Translation = Vector3.Zero,
                    Scale = Vector3.One
                };

                if (parent is not null)
                    tfm.SetParent(parent, false, EParentAssignmentMode.Immediate);

                tfm.RecalculateMatrices();
                cameras[i] = new(tfm, p);
            }
            return cameras;
        }
    }
}
