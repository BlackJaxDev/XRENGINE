using System.Numerics;
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
            FullScreenCubeMesh = new XRMeshRenderer(XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f), true), mat);
            FullScreenCubeMesh.SettingUniforms += SetUniforms;
        }

        private void SetUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
            => SettingUniforms?.Invoke(materialProgram);

        /// <summary>
        /// Renders the one side of the FBO to the entire region set by Engine.Rendering.State.PushRenderArea.
        /// </summary>
        public void RenderFullscreen(ECubemapFace face)
        {
            var cam = LocalCameras[(int)face];

            var state = Engine.Rendering.State.RenderingPipelineState;
            if (state is not null)
            {
                using (state.PushRenderingCamera(cam))
                    FullScreenCubeMesh.Render(Matrix4x4.Identity, null, 1, true);
            }
            else
            {
                Engine.Rendering.State.RenderingCameraOverride = cam;
                FullScreenCubeMesh.Render(Matrix4x4.Identity, null, 1, true);
                Engine.Rendering.State.RenderingCameraOverride = null;
            }
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
            Rotator[] rotations =
            [
                new(0.0f, -90.0f, 180.0f), //+X
                new(0.0f, 90.0f, 180.0f), //-X
                new(90.0f, 0.0f, 0.0f), //+Y
                new(-90.0f, 0.0f, 0.0f), //-Y
                new(0.0f, 180.0f, 180.0f), //+Z
                new(0.0f, 0.0f, 180.0f), //-Z
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
                    Parent = parent,
                    Rotation = rotations[i].ToQuaternion(),
                    Translation = Vector3.Zero,
                    Scale = Vector3.One
                };
                tfm.RecalcLocal();
                tfm.RecalcWorld(false);
                cameras[i] = new(tfm, p);
            }
            return cameras;
        }
    }
}
