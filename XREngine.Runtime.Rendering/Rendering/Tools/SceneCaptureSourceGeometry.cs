namespace XREngine.Rendering.Tools;

/// <summary>Retains exact borrowed renderer topology, mesh/material identities and geometry revisions for a capture.</summary>
internal static class SceneCaptureSourceGeometry
{
    internal static void ObserveRenderer(XRMeshRenderer renderer, List<Func<bool>> checks)
    {
        var submeshes = renderer.Submeshes;
        int count = submeshes.Count;
        XRMesh? primaryMesh = renderer.Mesh;
        XRMaterial? primaryMaterial = renderer.Material;
        long buffers = renderer.Buffers.MutationRevision;
        ulong skin = renderer.SkinnedOutputVersion, blend = renderer.BlendshapeWeightsVersion;
        checks.Add(() => !renderer.IsDestroyed && ReferenceEquals(renderer.Submeshes, submeshes) && submeshes.Count == count &&
            ReferenceEquals(renderer.Mesh, primaryMesh) && ReferenceEquals(renderer.Material, primaryMaterial) &&
            renderer.Buffers.MutationRevision == buffers && renderer.SkinnedOutputVersion == skin && renderer.BlendshapeWeightsVersion == blend);
        HashSet<XRMesh> meshes = [];
        ObserveMesh(primaryMesh);
        for (int index = 0; index < count; index++)
        {
            int slot = index;
            var submesh = renderer.Submeshes[index];
            XRMesh? mesh = submesh.Mesh;
            XRMaterial? material = submesh.Material;
            uint instances = submesh.InstanceCount;
            checks.Add(() => submeshes.Count == count && ReferenceEquals(submeshes[slot], submesh) &&
                ReferenceEquals(submesh.Mesh, mesh) && ReferenceEquals(submesh.Material, material) && submesh.InstanceCount == instances);
            ObserveMesh(mesh);
        }
        foreach (var entry in renderer.Buffers) ObserveBuffer(entry.Value);

        void ObserveMesh(XRMesh? mesh)
        {
            if (mesh is null || !meshes.Add(mesh)) return;
            long geometry = mesh.GeometryRevision, bufferRevision = mesh.Buffers.MutationRevision;
            checks.Add(() => !mesh.IsDestroyed && mesh.GeometryRevision == geometry && mesh.Buffers.MutationRevision == bufferRevision);
            foreach (var entry in mesh.Buffers) ObserveBuffer(entry.Value);
        }

        void ObserveBuffer(XRDataBuffer buffer)
        {
            ulong revision = buffer.Revision;
            checks.Add(() => !buffer.IsDestroyed && buffer.Revision == revision);
        }
    }

}
