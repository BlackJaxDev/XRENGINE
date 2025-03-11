using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine.Data.Components
{
    public class MirrorComponent : XRComponent
    {
        public ModelComponent Model => GetSiblingComponent<ModelComponent>(true)!;

        private float _mirrorHeight = 0.0f;
        public float MirrorHeight
        {
            get => _mirrorHeight;
            set => _mirrorHeight = value;
        }

        private float _mirrorWidth = 0.0f;
        public float MirrorWidth
        {
            get => _mirrorWidth;
            set => _mirrorWidth = value;
        }

        public Plane ReflectionPlane { get; private set; } = new Plane(Globals.Backward, 0);
        public Matrix4x4 ReflectionMatrix { get; private set; } = Matrix4x4.Identity;
        public AABB LocalCullingVolume { get; private set; } = new AABB(Vector3.Zero, Vector3.Zero);
        public Box WorldCullingVolume => LocalCullingVolume.ToBox(Transform.WorldMatrix);

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform)
        {
            base.OnTransformRenderWorldMatrixChanged(transform);
            ReflectionPlane = XRMath.CreatePlaneFromPointAndNormal(transform.WorldTranslation, transform.WorldForward);
            MakeReflectionMatrix();
        }

        private void MakeReflectionMatrix()
        {
            //ReflectionMatrix = Matrix4x4.CreateReflection(Plane);

            float Nx = ReflectionPlane.Normal.X;
            float Ny = ReflectionPlane.Normal.Y;
            float Nz = ReflectionPlane.Normal.Z;
            float D = ReflectionPlane.D;
            ReflectionMatrix = new Matrix4x4()
            {
                M11 =  1.0f - 2.0f * Nx * Nx,
                M12 = -2.0f * Nx * Ny,
                M13 = -2.0f * Nx * Nz,
                M14 =  0.0f,

                M21 = -2.0f * Ny * Nx,
                M22 =  1.0f - 2.0f * Ny * Ny,
                M23 = -2.0f * Ny * Nz,
                M24 =  0.0f,

                M31 = -2.0f * Nz * Nx,
                M32 = -2.0f * Nz * Ny,
                M33 =  1.0f - 2.0f * Nz * Nz,
                M34 =  0.0f,

                M41 = -2.0f * Nx * D,
                M42 = -2.0f * Ny * D,
                M43 = -2.0f * Nz * D,
                M44 =  1.0f
            };

            //Refl * View (Camera inv transform) = point transform into mirror space. do on GPU
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
            }
        }
    }
}
