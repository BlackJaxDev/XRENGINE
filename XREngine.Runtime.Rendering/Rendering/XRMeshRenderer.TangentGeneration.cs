using XREngine.Data.Core;

namespace XREngine.Rendering
{
    public partial class XRMeshRenderer
    {
        /// <summary>
        /// Adds UV0 tangents to the mesh when the material asks for them and the mesh has none.
        /// </summary>
        /// <remarks>
        /// The check runs when the mesh or the material changes, and when
        /// <see cref="XRMaterial.GenerateMissingMeshTangents"/> of the current material changes.
        /// </remarks>
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (propName == nameof(Material))
            {
                if (prev is XRMaterial previousMaterial)
                    previousMaterial.PropertyChanged -= OnMaterialPropertyChangedForTangents;
                if (field is XRMaterial currentMaterial)
                    currentMaterial.PropertyChanged += OnMaterialPropertyChangedForTangents;
            }

            if (propName is nameof(Mesh) or nameof(Material))
                GenerateMissingMeshTangents();
        }

        private void OnMaterialPropertyChangedForTangents(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(XRMaterial.GenerateMissingMeshTangents) && ReferenceEquals(sender, _material))
                GenerateMissingMeshTangents();
        }

        private void GenerateMissingMeshTangents()
        {
            XRMesh? mesh = _mesh;
            if (mesh is null || mesh.HasTangents || _material is not { GenerateMissingMeshTangents: true })
                return;

            // A shared mesh gets the tangents for all of its renderers. Programs that do not read them ignore the buffer.
            if (!mesh.GenerateTangentsFromUv0())
                Debug.LogWarning($"[XRMeshRenderer] Material '{_material.Name}' needs mesh tangents, but mesh '{mesh.Name}' has no triangles, normals, or usable UV0 to generate them.");
        }
    }
}
