namespace XREngine.Rendering
{
    public partial class XRMeshRenderer
    {
        /// <summary>
        /// Adds UV0 tangents to the mesh when the material asks for them and the mesh has none.
        /// </summary>
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (propName is nameof(Mesh) or nameof(Material))
                GenerateMissingMeshTangents();
        }

        private void GenerateMissingMeshTangents()
        {
            XRMesh? mesh = _mesh;
            if (mesh is null || mesh.HasTangents || _material is not { GenerateMissingMeshTangents: true })
                return;

            if (!mesh.GenerateTangentsFromUv0())
                Debug.LogWarning($"[XRMeshRenderer] Material '{_material.Name}' needs mesh tangents, but mesh '{mesh.Name}' has no triangles, normals, or usable UV0 to generate them.");
        }
    }
}
