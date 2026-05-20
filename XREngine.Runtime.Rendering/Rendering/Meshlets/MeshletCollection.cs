using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;

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
        private XRDataBuffer? _commandVisibilityBuffer;

        private readonly Dictionary<uint, (int offsetIndex, int count)> _meshletOffsets = [];
        private readonly List<Meshlet> _meshlets = [];
        private readonly List<MeshletVertex> _vertices = [];
        private readonly List<MeshletMaterial> _materials = [];
        private readonly Dictionary<uint, Matrix4x4> _transforms = [];
        private readonly List<uint> _commandVisibilityValues = [];

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
            var taskShader = ShaderHelper.LoadEngineShader(Path.Combine("Meshlets", "MeshletCullingDiagnostic.task"), EShaderType.Task);
            var meshShader = ShaderHelper.LoadEngineShader(Path.Combine("Meshlets", "MeshletRenderDiagnostic.mesh"), EShaderType.Mesh);
            var fragmentShader = ShaderHelper.LoadEngineShader(Path.Combine("Meshlets", "MeshletShading.fs"), EShaderType.Fragment);

            _taskMeshProgram = new XRRenderProgram(true, false, taskShader, meshShader, fragmentShader);

            // Create buffers
            _meshletBuffer = CreateBuffer("MeshletBuffer", EBufferTarget.ShaderStorageBuffer);
            _visibleMeshletBuffer = CreateBuffer("VisibleMeshletBuffer", EBufferTarget.ShaderStorageBuffer);
            _vertexBuffer = CreateBuffer("VertexBuffer", EBufferTarget.ShaderStorageBuffer);
            _indexBuffer = CreateBuffer("IndexBuffer", EBufferTarget.ShaderStorageBuffer);
            _triangleBuffer = CreateBuffer("TriangleBuffer", EBufferTarget.ShaderStorageBuffer);
            _transformBuffer = CreateBuffer("TransformBuffer", EBufferTarget.ShaderStorageBuffer);
            _materialBuffer = CreateBuffer("MaterialBuffer", EBufferTarget.ShaderStorageBuffer);
            _commandVisibilityBuffer = CreateBuffer("CommandVisibilityBuffer", EBufferTarget.ShaderStorageBuffer);
        }

        private static XRDataBuffer CreateBuffer(string name, EBufferTarget target) => new(name, target, false)
        {
            Usage = EBufferUsage.DynamicDraw,
            DisposeOnPush = false
        };

        /// <summary>
        /// Add a mesh to the meshlet renderer
        /// </summary>
        public void AddMesh(XRMesh mesh, uint instanceID, uint materialID, int renderPass, Matrix4x4 transform, MeshletGenerationSettings? settings = null)
        {
            settings ??= new MeshletGenerationSettings
            {
                Enabled = true,
                BuildMode = MeshletBuildMode.Dense,
            };

            if (!settings.Enabled)
                return;

            if (!mesh.TryGetFreshMeshletPayload(settings, lodSettings: null, sourceMeshIdentity: null, out MeshletPayload payload) ||
                !payload.HasMeshlets)
            {
                return;
            }

            // Adjust offsets in returned meshlets and append to global lists
            uint baseVertexOffset = (uint)_vertices.Count;
            uint baseVertOffset = (uint)_vertexIndices.Count;
            uint baseTriOffset = (uint)_triangleIndices.Count;

            for (int i = 0; i < payload.Meshlets.Length; i++)
            {
                Meshlet m = payload.Meshlets[i].ToGpuMeshlet(instanceID, materialID, (uint)renderPass);
                m.MeshID = instanceID;
                m.MaterialID = materialID;
                m.RenderPass = (uint)renderPass;
                m.VertexOffset += baseVertOffset;
                m.TriangleOffset += baseTriOffset;
                _meshlets.Add(m);
            }

            _vertices.AddRange(payload.Vertices);
            for (int i = 0; i < payload.VertexIndices.Length; i++)
                _vertexIndices.Add(payload.VertexIndices[i] + baseVertexOffset);
            _triangleIndices.AddRange(payload.TriangleIndices);

            _meshletOffsets[instanceID] = (_meshlets.Count - payload.Meshlets.Length, payload.Meshlets.Length);
            _transforms[instanceID] = transform;
            _buffersDirty = true;
            _transformBufferDirty = true;
        }

        /// <summary>
        /// Add a material to the renderer
        /// </summary>
        public void AddMaterial(uint materialID, MeshletMaterial material)
        {
            // Ensure materials list is large enough
            while (_materials.Count <= materialID)
                _materials.Add(default);
            
            _materials[(int)materialID] = material;
            _materialBufferDirty = true;
        }

        /// <summary>
        /// Update transform for a mesh
        /// </summary>
        public void UpdateTransform(uint instanceID, Matrix4x4 transform)
        {
            _transforms[instanceID] = transform;
            _transformBufferDirty = true;
        }

        private static uint[] PackTriangleIndices(IReadOnlyList<byte> triangleIndices)
        {
            if (triangleIndices.Count == 0)
                return [];

            int packedLength = (triangleIndices.Count + 3) / 4;
            uint[] packed = new uint[packedLength];
            for (int i = 0; i < triangleIndices.Count; i++)
                packed[i >> 2] |= (uint)triangleIndices[i] << ((i & 3) * 8);

            return packed;
        }

        private bool _buffersDirty = true;
        private bool _materialBufferDirty = true;
        private bool _transformBufferDirty = true;
        private bool _commandVisibilityBufferDirty = true;

        private void UpdateBuffers()
        {
            EnsureInitialized();

            if (_buffersDirty)
            {
                _meshletBuffer?.SetDataRaw(_meshlets.ToArray());
                _vertexBuffer?.SetDataRaw(_vertices.ToArray());

                // Vertex remap indices per meshlet
                _indexBuffer?.SetDataRaw(_vertexIndices.ToArray());

                // Triangle local indices per meshlet
                _triangleBuffer?.SetDataRaw(PackTriangleIndices(_triangleIndices));
                
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
                var maxMeshID = GetTransformBufferElementCount();
                var transformArray = new Matrix4x4[maxMeshID];
                
                foreach (var kvp in _transforms)
                    transformArray[kvp.Key] = kvp.Value;
                
                _transformBuffer?.SetDataRaw(transformArray);
                _transformBufferDirty = false;
                _commandVisibilityBufferDirty = true;
            }
        }

        private int GetTransformBufferElementCount()
        {
            if (_transforms.Count == 0)
                return 1;

            uint maxMeshID = 0u;
            foreach (uint meshID in _transforms.Keys)
                maxMeshID = Math.Max(maxMeshID, meshID);
            return checked((int)maxMeshID + 1);
        }

        private void UpdateCommandVisibilityBuffer(GPUScene? scene, Func<GPUScene, uint, bool>? commandVisibility)
        {
            int elementCount = GetTransformBufferElementCount();
            while (_commandVisibilityValues.Count < elementCount)
                _commandVisibilityValues.Add(1u);
            if (_commandVisibilityValues.Count > elementCount)
                _commandVisibilityValues.RemoveRange(elementCount, _commandVisibilityValues.Count - elementCount);

            if (commandVisibility is null || scene is null)
            {
                if (!_commandVisibilityBufferDirty)
                    return;

                for (int i = 0; i < _commandVisibilityValues.Count; i++)
                    _commandVisibilityValues[i] = 1u;
            }
            else
            {
                for (int i = 0; i < _commandVisibilityValues.Count; i++)
                    _commandVisibilityValues[i] = 0u;
                foreach (uint meshID in _transforms.Keys)
                    _commandVisibilityValues[(int)meshID] = commandVisibility(scene, meshID) ? 1u : 0u;
            }

            _commandVisibilityBuffer?.SetDataRaw(CollectionsMarshal.AsSpan(_commandVisibilityValues));
            _commandVisibilityBufferDirty = commandVisibility is not null && scene is not null;
        }

        /// <summary>
        /// Issue an OpenGL NV_mesh_shader draw using current meshlet data.
        /// </summary>
        public bool Render(
            XRCamera camera,
            int renderPass,
            GPUScene? visibilityScene = null,
            Func<GPUScene, uint, bool>? commandVisibility = null,
            bool meshletDebugDisplay = false)
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
            UpdateCommandVisibilityBuffer(visibilityScene, commandVisibility);

            gl.RawGL.Disable(Silk.NET.OpenGL.EnableCap.CullFace);
            gl.RawGL.Disable(Silk.NET.OpenGL.EnableCap.StencilTest);
            gl.RawGL.Disable(Silk.NET.OpenGL.EnableCap.Blend);

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
            if (_commandVisibilityBuffer is not null)
                _taskMeshProgram.BindBuffer(_commandVisibilityBuffer, 7);

            // Set camera uniforms expected by shaders
            var viewMatrix = camera.Transform.InverseRenderMatrix;
            var viewProjectionMatrix = viewMatrix * camera.ProjectionMatrix;
            var cameraPosition = camera.Transform.RenderTranslation;
            var frustumPlanes = camera.WorldFrustum().Planes.Select(x => x.AsVector4()).ToArray();

            _taskMeshProgram.Uniform("ViewProjectionMatrix", viewProjectionMatrix);
            _taskMeshProgram.Uniform("ViewMatrix", viewMatrix);
            _taskMeshProgram.Uniform("cameraPosition", cameraPosition);
            _taskMeshProgram.Uniform("RenderPass", renderPass);
            _taskMeshProgram.Uniform("UseCpuCommandVisibility", commandVisibility is not null && visibilityScene is not null ? 1u : 0u);
            _taskMeshProgram.Uniform("EnableMeshletDebugDisplay", meshletDebugDisplay ? 1u : 0u);
            _taskMeshProgram.Uniform("lightDirection", Vector3.Normalize(new Vector3(-0.35f, -1.0f, -0.25f)));
            _taskMeshProgram.Uniform("lightColor", Vector3.One);
            _taskMeshProgram.Uniform("lightIntensity", 2.0f);

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
            _meshletOffsets.Clear();
            _vertices.Clear();
            _materials.Clear();
            _transforms.Clear();
            _vertexIndices.Clear();
            _triangleIndices.Clear();
            _commandVisibilityValues.Clear();
            _buffersDirty = true;
            _materialBufferDirty = true;
            _transformBufferDirty = true;
            _commandVisibilityBufferDirty = true;
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
            _commandVisibilityBuffer?.Dispose();
        }
    }
}
