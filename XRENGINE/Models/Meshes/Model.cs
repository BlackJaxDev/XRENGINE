using MemoryPack;
using XREngine.Data;
using XREngine.Core.Files;
using XREngine.Components.Scene.Mesh;
using System.Collections.Generic;

namespace XREngine.Rendering.Models
{
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
    }
}
