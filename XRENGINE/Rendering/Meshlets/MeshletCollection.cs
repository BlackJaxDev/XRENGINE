using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
using static Meshoptimizer.Meshopt;

namespace XREngine.Rendering.Meshlets
{
    /// <summary>
    /// Manages meshlet-based rendering using task and mesh shaders
    /// </summary>
    public class MeshletCollection : IDisposable
    {
        private XRRenderProgram? _taskMeshProgram;
        private XRDataBuffer? _meshletBuffer;
        private XRDataBuffer? _visibleMeshletBuffer;
        private XRDataBuffer? _vertexBuffer;
        private XRDataBuffer? _indexBuffer;
        private XRDataBuffer? _triangleBuffer;
        private XRDataBuffer? _transformBuffer;
        private XRDataBuffer? _materialBuffer;

        private readonly Dictionary<uint, (int offsetIndex, int count)> _meshletOffsets = [];
        private readonly List<Meshlet> _meshlets = [];
        private readonly List<MeshletMaterial> _materials = [];
        private readonly Dictionary<uint, Matrix4x4> _transforms = [];

        // Persisted index arrays for all meshlets in this collection
        private readonly List<uint> _vertexIndices = [];
        private readonly List<byte> _triangleIndices = [];

        private bool _initialized = false;

        public MeshletCollection()
        {
        }

        private void EnsureInitialized()
        {
            if (_initialized)
                return;
            Initialize();
            _initialized = true;
        }

        private void Initialize()
        {
            // Create shader program with task and mesh shaders
            var taskShader = ShaderHelper.LoadEngineShader(Path.Combine("Meshlets", "MeshletCulling.task"), EShaderType.Task);
            var meshShader = ShaderHelper.LoadEngineShader(Path.Combine("Meshlets", "MeshletRender.mesh"), EShaderType.Mesh);
            var fragmentShader = ShaderHelper.LoadEngineShader(Path.Combine("Meshlets", "MeshletShading.fs"), EShaderType.Fragment);

            _taskMeshProgram = new XRRenderProgram(false, true, taskShader, meshShader, fragmentShader);

            // Create buffers
            _meshletBuffer = CreateBuffer("MeshletBuffer", EBufferTarget.ShaderStorageBuffer);
            _visibleMeshletBuffer = CreateBuffer("VisibleMeshletBuffer", EBufferTarget.ShaderStorageBuffer);
            _vertexBuffer = CreateBuffer("VertexBuffer", EBufferTarget.ShaderStorageBuffer);
            _indexBuffer = CreateBuffer("IndexBuffer", EBufferTarget.ShaderStorageBuffer);
            _triangleBuffer = CreateBuffer("TriangleBuffer", EBufferTarget.ShaderStorageBuffer);
            _transformBuffer = CreateBuffer("TransformBuffer", EBufferTarget.ShaderStorageBuffer);
            _materialBuffer = CreateBuffer("MaterialBuffer", EBufferTarget.ShaderStorageBuffer);
        }

        private static XRDataBuffer CreateBuffer(string name, EBufferTarget target) => new(name, target, false)
        {
            Usage = EBufferUsage.DynamicDraw,
            DisposeOnPush = false
        };

        /// <summary>
        /// Add a mesh to the meshlet renderer
        /// </summary>
        public void AddMesh(XRMesh mesh, uint meshID, uint materialID, Matrix4x4 transform)
        {
            EnsureInitialized();

            var meshletData = MeshletGenerator.Build([mesh], out var vertexIndices, out var triangleIndices);

            // Adjust offsets in returned meshlets and append to global lists
            uint baseVertOffset = (uint)_vertexIndices.Count;
            byte baseTriOffset = (byte)(_triangleIndices.Count / 3); // 3 indices per triangle
            for (int i = 0; i < meshletData.Length; i++)
            {
                var m = meshletData[i];
                m.VertexOffset += baseVertOffset;
                m.TriangleOffset += baseTriOffset;
                _meshlets.Add(m);
            }

            _vertexIndices.AddRange(vertexIndices);
            _triangleIndices.AddRange(triangleIndices);

            _meshletOffsets[meshID] = (_meshlets.Count - meshletData.Length, meshletData.Length);
            _transforms[meshID] = transform;
            _buffersDirty = true;
        }

        /// <summary>
        /// Add a material to the renderer
        /// </summary>
        public void AddMaterial(uint materialID, MeshletMaterial material)
        {
            EnsureInitialized();

            // Ensure materials list is large enough
            while (_materials.Count <= materialID)
                _materials.Add(default);
            
            _materials[(int)materialID] = material;
            _materialBufferDirty = true;
        }

        /// <summary>
        /// Update transform for a mesh
        /// </summary>
        public void UpdateTransform(uint meshID, Matrix4x4 transform)
        {
            EnsureInitialized();
            _transforms[meshID] = transform;
            _transformBufferDirty = true;
        }

        private bool _buffersDirty = true;
        private bool _materialBufferDirty = true;
        private bool _transformBufferDirty = true;

        private void UpdateBuffers()
        {
            EnsureInitialized();

            if (_buffersDirty)
            {
                _meshletBuffer?.SetDataRaw(_meshlets.ToArray());

                // Vertex remap indices per meshlet
                _indexBuffer?.SetDataRaw(_vertexIndices.ToArray());

                // Triangle local indices per meshlet
                _triangleBuffer?.SetDataRaw(_triangleIndices.ToArray());
                
                // Initialize visible meshlet buffer (counter + max meshlets)
                var visibleMeshletData = new uint[1 + _meshlets.Count];
                visibleMeshletData[0] = 0; // Counter
                _visibleMeshletBuffer?.SetDataRaw(visibleMeshletData);
                
                _buffersDirty = false;
            }

            if (_materialBufferDirty)
            {
                _materialBuffer?.SetData(_materials.ToArray());
                _materialBufferDirty = false;
            }

            if (_transformBufferDirty)
            {
                // Convert dictionary to array indexed by meshID
                var maxMeshID = _transforms.Keys.Count > 0 ? Math.Max(1, _transforms.Keys.Max() + 1) : 1;
                var transformArray = new Matrix4x4[maxMeshID];
                
                foreach (var kvp in _transforms)
                    transformArray[kvp.Key] = kvp.Value;
                
                _transformBuffer?.SetDataRaw(transformArray);
                _transformBufferDirty = false;
            }
        }

        /// <summary>
        /// Issue an OpenGL NV_mesh_shader draw using current meshlet data.
        /// </summary>
        public bool Render(XRCamera camera)
        {
            EnsureInitialized();

            if (_taskMeshProgram is null)
                return false;

            if (AbstractRenderer.Current is not OpenGLRenderer gl)
                return false;

            if (gl.NVMeshShader is null)
            {
                Debug.LogWarning("NV_mesh_shader not supported on current OpenGL context.");
                return false;
            }

            if (_meshlets.Count == 0)
                return false;

            UpdateBuffers();

            // Use task/mesh program
            _taskMeshProgram.Use();

            // Bind SSBOs expected by shaders
            if (_meshletBuffer is not null)
                _taskMeshProgram.BindBuffer(_meshletBuffer, 0);
            if (_visibleMeshletBuffer is not null)
                _taskMeshProgram.BindBuffer(_visibleMeshletBuffer, 1);
            if (_vertexBuffer is not null)
                _taskMeshProgram.BindBuffer(_vertexBuffer, 2);
            if (_indexBuffer is not null)
                _taskMeshProgram.BindBuffer(_indexBuffer, 3);
            if (_triangleBuffer is not null)
                _taskMeshProgram.BindBuffer(_triangleBuffer, 4);
            if (_transformBuffer is not null)
                _taskMeshProgram.BindBuffer(_transformBuffer, 5);
            if (_materialBuffer is not null)
                _taskMeshProgram.BindBuffer(_materialBuffer, 6);

            // Set camera uniforms expected by shaders
            var viewMatrix = camera.Transform.InverseRenderMatrix;
            var viewProjectionMatrix = camera.ProjectionMatrix * viewMatrix;
            var cameraPosition = camera.Transform.RenderTranslation;
            var frustumPlanes = camera.WorldFrustum().Planes.Select(x => x.AsVector4()).ToArray();

            _taskMeshProgram.Uniform("ViewProjectionMatrix", viewProjectionMatrix);
            _taskMeshProgram.Uniform("ViewMatrix", viewMatrix);
            _taskMeshProgram.Uniform("cameraPosition", cameraPosition);

            for (int i = 0; i < Math.Min(frustumPlanes.Length, 6); i++)
                _taskMeshProgram.Uniform($"FrustumPlanes[{i}]", frustumPlanes[i]);

            // Reset visible meshlet counter
            _visibleMeshletBuffer?.Set<uint>(0, 0);

            // Draw mesh tasks; group size must match local_size_x in task shader (assumed 32)
            uint groupSize = 32;
            uint numMeshlets = (uint)_meshlets.Count;
            uint numGroups = (numMeshlets + groupSize - 1) / groupSize;
            if (numGroups == 0)
                return false;

            gl.NVMeshShader.DrawMeshTask(0, numGroups);
            return true;
        }

        /// <summary>
        /// Clear all meshlets and reset
        /// </summary>
        public void Clear()
        {
            _meshlets.Clear();
            _materials.Clear();
            _transforms.Clear();
            _vertexIndices.Clear();
            _triangleIndices.Clear();
            _buffersDirty = true;
            _materialBufferDirty = true;
            _transformBufferDirty = true;
        }

        public void Dispose()
        {
            _taskMeshProgram?.Destroy();
            _meshletBuffer?.Dispose();
            _visibleMeshletBuffer?.Dispose();
            _vertexBuffer?.Dispose();
            _indexBuffer?.Dispose();
            _triangleBuffer?.Dispose();
            _transformBuffer?.Dispose();
            _materialBuffer?.Dispose();
        }
    }
}
