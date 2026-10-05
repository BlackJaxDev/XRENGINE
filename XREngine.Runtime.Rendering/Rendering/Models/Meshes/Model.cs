using MemoryPack;
using XREngine.Data;
using XREngine.Core.Files;
using XREngine.Components.Scene.Mesh;
using System.Collections.Generic;

namespace XREngine.Rendering.Models
{
    [XRAssetInspector("XREngine.Editor.AssetEditors.ModelInspector")]
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class Model : XRAsset
    {
        public Model() { }
        public Model(params SubMesh[] meshes)
            => _meshes.AddRange(meshes);
        public Model(IEnumerable<SubMesh> meshes)
            => _meshes.AddRange(meshes);

        protected EventList<SubMesh> _meshes = [];
        public EventList<SubMesh> Meshes
        {
            get => _meshes;
            set => SetField(ref _meshes, value ?? []);
        }

        /// <summary>
        /// Destroys the owned submesh list. It is a registered engine object, so the global
        /// object cache would otherwise keep the submeshes, their meshes and CPU vertex data
        /// reachable after this model is destroyed.
        /// </summary>
        protected override void OnDestroying()
        {
            _meshes.Destroy(true);
            base.OnDestroying();
        }
    }
}
