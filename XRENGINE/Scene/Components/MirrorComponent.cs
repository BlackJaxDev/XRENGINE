using Extensions;
using MathNet.Numerics;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;

namespace XREngine.Data.Components
{
    public class MirrorComponent : XRComponent, IRenderable
    {
        public MirrorComponent()
        {
            XRMesh mesh = XRMesh.Create(VertexQuad.PosZ(1.0f));
            XRMaterial mat = XRMaterial.CreateUnlitColorMaterialForward();
            mat.RenderPass = (int)EDefaultRenderPass.Background;
            //Mirrors will increase the stencil buffer by 1
            mat.RenderOptions = new()
            {
                //Render the front and back faces of the mesh
                CullMode = ECullMode.None,
                //Increment the stencil buffer value if the mesh renders
                StencilTest = new StencilTest()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    FrontFace = MirrorStencil(),
                    BackFace = MirrorStencil(),
                },
                //Depth test is enabled like a regular object
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    Function = XREngine.Rendering.Models.Materials.EComparison.Less,
                    UpdateDepth = true,
                },
                //Don't write to the color buffer, only the stencil buffer
                WriteAlpha = false,
                WriteBlue = false,
                WriteGreen = false,
                WriteRed = false,
            };
            XRMeshRenderer rend = new(mesh, mat);
            _rcMirror = new RenderCommandMesh3D((int)EDefaultRenderPass.Background, rend, Matrix4x4.Identity);
            RenderedObjects =
            [
                RenderInfo3D.New(this, _rcMirror)
            ];
        }

        private static StencilTestFace MirrorStencil() => new()
        {
            Function = XREngine.Rendering.Models.Materials.EComparison.Always, //Always pass and increment
            BothFailOp = EStencilOp.Keep, //Keep the current value if depth test fails and the mesh doesn't render
            StencilPassDepthFailOp = EStencilOp.Keep, //Keep the current value if depth test fails and the mesh doesn't render
            BothPassOp = EStencilOp.Incr, //Increment the stencil buffer value if depth test passes and the mesh renders
            Reference = 0, //We're not testing against the stencil buffer value, so this is 0
            ReadMask = 0, //We're not testing against the stencil buffer value, so this is 0
            WriteMask = GetMaxMirrorBitMask(),
        };

        private static uint GetMaxMirrorBitMask()
        {
            int maxMirrors = ((int)Engine.GameSettings.MaxMirrorRecursionCount).CeilingToPowerOfTwo().Clamp(0, 16);
            int initialShift = 0;
            uint mask = 0;
            for (int i = 0; i < maxMirrors; i++)
                mask |= (uint)(1 << (initialShift + i));
            return mask;
        }

        private readonly RenderCommandMesh3D _rcMirror;

        private float _mirrorHeight = 0.0f;
        public float MirrorHeight
        {
            get => _mirrorHeight;
            set => SetField(ref _mirrorHeight, value);
        }

        private float _mirrorWidth = 0.0f;
        public float MirrorWidth
        {
            get => _mirrorWidth;
            set => SetField(ref _mirrorWidth, value);
        }

        public Plane ReflectionPlane { get; private set; } = new Plane(Globals.Backward, 0);
        public Matrix4x4 ReflectionMatrix { get; private set; } = Matrix4x4.Identity;
        public AABB LocalCullingVolume { get; private set; } = new AABB(Vector3.Zero, Vector3.Zero);
        public Box WorldCullingVolume { get; private set; } = new Box(Vector3.Zero, Vector3.Zero, Matrix4x4.Identity);
        public RenderInfo[] RenderedObjects { get; }

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform)
        {
            base.OnTransformRenderWorldMatrixChanged(transform);

            ReflectionPlane = XRMath.CreatePlaneFromPointAndNormal(transform.RenderTranslation, transform.RenderForward);
            MakeReflectionMatrix();
            WorldCullingVolume = LocalCullingVolume.ToBox(Transform.RenderMatrix);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(MirrorHeight):
                case nameof(MirrorWidth):
                    LocalCullingVolume = new AABB(
                        new Vector3(0, 0, 0),
                        new Vector3(MirrorWidth, MirrorHeight, 0.001f));
                    break;
                case nameof(LocalCullingVolume):
                    WorldCullingVolume = LocalCullingVolume.ToBox(Transform.RenderMatrix);
                    break;
                case nameof(WorldCullingVolume):

                    break;
            }
        }

        private void MakeReflectionMatrix()
        {
            ReflectionMatrix = Matrix4x4.CreateReflection(ReflectionPlane);

            //float Nx = ReflectionPlane.Normal.X;
            //float Ny = ReflectionPlane.Normal.Y;
            //float Nz = ReflectionPlane.Normal.Z;
            //float D = ReflectionPlane.D;
            //ReflectionMatrix = new Matrix4x4()
            //{
            //    M11 =  1.0f - 2.0f * Nx * Nx,
            //    M12 = -2.0f * Nx * Ny,
            //    M13 = -2.0f * Nx * Nz,
            //    M14 =  0.0f,

            //    M21 = -2.0f * Ny * Nx,
            //    M22 =  1.0f - 2.0f * Ny * Ny,
            //    M23 = -2.0f * Ny * Nz,
            //    M24 =  0.0f,

            //    M31 = -2.0f * Nz * Nx,
            //    M32 = -2.0f * Nz * Ny,
            //    M33 =  1.0f - 2.0f * Nz * Nz,
            //    M34 =  0.0f,

            //    M41 = -2.0f * Nx * D,
            //    M42 = -2.0f * Ny * D,
            //    M43 = -2.0f * Nz * D,
            //    M44 =  1.0f
            //};

            //Refl * View (Camera inv transform) = point transform into mirror space. do on GPU
        }
    }
}
