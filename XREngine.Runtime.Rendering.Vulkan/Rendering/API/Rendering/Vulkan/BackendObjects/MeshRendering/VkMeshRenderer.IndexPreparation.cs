namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    // Owned by _bufferStateSync alongside the mesh's index bindings.
    private XRMesh? _indexPreparationMesh;
    private long _indexPreparationGeometryRevision;

    private void RequestIndexPreparationBeforeDrawAdmission()
    {
        lock (_bufferStateSync)
        {
            // External atlases own their indices. A known shader-generated draw
            // does not need mesh index streams; EnsureBuffers handles program changes.
            if (_triangleIndexBufferExternallyProvided || ProgramUsesShaderGeneratedVertices() ||
                Mesh is not { } mesh)
                return;

            long revision = mesh.GeometryRevision;
            if (ReferenceEquals(_indexPreparationMesh, mesh) &&
                _indexPreparationGeometryRevision == revision)
                return;

            mesh.RequestIndexBufferPreparation();
            if (!mesh.AreIndexBufferBindingsCurrent(
                    _triangleIndexBuffer?.Data, _lineIndexBuffer?.Data, _pointIndexBuffer?.Data))
            {
                // Invalidate reuse once per observed revision, not once per pending
                // poll. EnsureBuffers owns binding/upload publication and its existing
                // readiness snapshot keeps admission pending until work completes.
                ApplyIndexBufferReadyNoLock();
            }

            _indexPreparationMesh = mesh;
            _indexPreparationGeometryRevision = revision;
        }
    }
}
