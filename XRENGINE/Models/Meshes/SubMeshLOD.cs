using XREngine.Data.Core;

namespace XREngine.Rendering.Models
{
    public class SubMeshLOD(
        XRMaterial? material,
        XRMesh? mesh,
        float maxVisibleDistance) : XRBase
    {
        public SubMeshLOD() 
            : this(null, null, 0) { }

        /// <summary>
        /// The material to use for this LOD.
        /// </summary>
        public XRMaterial? Material
        {
            get => material;
            set => SetField(ref material, value);
        }

        /// <summary>
        /// The mesh to render for this LOD.
        /// </summary>
        public XRMesh? Mesh
        {
            get => mesh;
            set => SetField(ref mesh, value);
        }

        public float MaxVisibleDistance
        {
            get => maxVisibleDistance;
            set => SetField(ref maxVisibleDistance, value);
        }

        public XRMeshRenderer NewRenderer()
            => new(mesh, material) { GenerateAsync = false };
    }
}
