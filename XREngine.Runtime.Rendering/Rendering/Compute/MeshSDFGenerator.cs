using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute
{
    /// <summary>
    /// Generates Signed Distance Fields (SDF) from mesh data using GPU compute shaders.
    /// This class handles the initialization and execution of the MeshSDFGen compute shader.
    /// </summary>
    public class MeshSDFGenerator : XRBase, IDisposable
    {
        #region Private Fields

        private XRRenderProgram? _computeProgram;
        private XRShader? _meshSDFShader;
        private XRTexture3D? _sdfTexture;
        private XRDataBuffer? _verticesBuffer;
        private XRDataBuffer? _indicesBuffer;
        private XRDataBuffer? _spatialNodesBuffer;
        private XRDataBuffer? _triangleToNodeBuffer;
        private bool _isInitialized = false;

        // Configuration parameters
        private float _maxDistance = 100.0f;
        private bool _useSpatialAcceleration = true;
        private float _epsilon = 1e-6f;
        private int _maxIterations = 1000;

        #endregion

        #region Properties

        /// <summary>
        /// The generated SDF texture containing signed distance values.
        /// </summary>
        public XRTexture3D? SDFTexture => _sdfTexture;

        /// <summary>
        /// Whether the SDF generator has been initialized.
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Maximum distance to consider for SDF generation. Points beyond this distance will be clamped.
        /// </summary>
        public float MaxDistance
        {
            get => _maxDistance;
            set => SetField(ref _maxDistance, Math.Max(0.1f, value));
        }

        /// <summary>
        /// Whether to use spatial acceleration for better performance.
        /// </summary>
        public bool UseSpatialAcceleration
        {
            get => _useSpatialAcceleration;
            set => SetField(ref _useSpatialAcceleration, value);
        }

        /// <summary>
        /// Numerical precision epsilon for triangle intersection tests.
        /// </summary>
        public float Epsilon
        {
            get => _epsilon;
            set => SetField(ref _epsilon, Math.Max(1e-8f, Math.Min(1e-3f, value)));
        }

        /// <summary>
        /// Maximum iterations per voxel for early termination.
        /// </summary>
        public int MaxIterations
        {
            get => _maxIterations;
            set => SetField(ref _maxIterations, Math.Max(100, Math.Min(10000, value)));
        }

        #endregion

        #region Constructor and Initialization

        public MeshSDFGenerator()
        {
        }

        /// <summary>
        /// Initializes the SDF generator with the compute shader and resources.
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
                return;

            try
            {
                // Load the compute shader
                _meshSDFShader = ShaderHelper.LoadEngineShader("Compute/SDF/MeshSDFGen.comp", EShaderType.Compute);
                
                // Create the render program
                _computeProgram = new XRRenderProgram(true, false, _meshSDFShader);
                
                _isInitialized = true;
                Debug.Out("MeshSDFGenerator initialized successfully");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to initialize MeshSDFGenerator: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Cleans up resources used by the SDF generator.
        /// </summary>
        public void Cleanup()
        {
            if (!_isInitialized)
                return;

            _computeProgram?.Destroy();
            _computeProgram = null;
            
            _meshSDFShader?.Destroy();
            _meshSDFShader = null;
            
            _sdfTexture?.Destroy();
            _sdfTexture = null;
            
            _verticesBuffer?.Destroy();
            _verticesBuffer = null;
            
            _indicesBuffer?.Destroy();
            _indicesBuffer = null;
            
            _spatialNodesBuffer?.Destroy();
            _spatialNodesBuffer = null;
            
            _triangleToNodeBuffer?.Destroy();
            _triangleToNodeBuffer = null;
            
            _isInitialized = false;
        }

        #endregion

        #region SDF Generation

        /// <summary>
        /// Generates a Signed Distance Field from the provided mesh data.
        /// </summary>
        /// <param name="mesh">The mesh to generate SDF from</param>
        /// <param name="resolution">The resolution of the 3D SDF texture (x, y, z)</param>
        /// <param name="padding">Additional padding around the mesh bounds (in world units)</param>
        /// <returns>The generated SDF texture, or null if generation failed</returns>
        public XRTexture3D? GenerateSDF(XRMesh mesh, IVector3 resolution, float padding = 0.1f)
        {
            if (!_isInitialized)
            {
                Debug.Out("MeshSDFGenerator must be initialized before generating SDF");
                return null;
            }

            if (mesh == null)
            {
                Debug.LogWarning("Mesh cannot be null");
                return null;
            }

            try
            {
                // Calculate bounds with padding
                var bounds = CalculateBoundsWithPadding(mesh, padding);
                
                // Create or recreate the SDF texture
                CreateSDFTexture(resolution);
                
                // Prepare mesh data buffers
                PrepareMeshBuffers(mesh);
                
                // Execute the compute shader
                ExecuteComputeShader(bounds, resolution);
                
                Debug.Out($"SDF generated successfully with resolution {resolution}");
                return _sdfTexture;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to generate SDF: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Generates a Signed Distance Field with custom bounds.
        /// </summary>
        /// <param name="mesh">The mesh to generate SDF from</param>
        /// <param name="resolution">The resolution of the 3D SDF texture (x, y, z)</param>
        /// <param name="minBounds">Minimum bounds of the SDF volume</param>
        /// <param name="maxBounds">Maximum bounds of the SDF volume</param>
        /// <returns>The generated SDF texture, or null if generation failed</returns>
        public XRTexture3D? GenerateSDF(XRMesh mesh, IVector3 resolution, Vector3 minBounds, Vector3 maxBounds)
        {
            if (!_isInitialized)
            {
                Debug.LogWarning("MeshSDFGenerator must be initialized before generating SDF");
                return null;
            }

            if (mesh == null)
            {
                Debug.LogWarning("Mesh cannot be null");
                return null;
            }

            try
            {
                // Create or recreate the SDF texture
                CreateSDFTexture(resolution);
                
                // Prepare mesh data buffers
                PrepareMeshBuffers(mesh);
                
                // Execute the compute shader with custom bounds
                var bounds = new AABB(minBounds, maxBounds);
                ExecuteComputeShader(bounds, resolution);
                
                Debug.Out($"SDF generated successfully with custom bounds and resolution {resolution}");
                return _sdfTexture;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to generate SDF: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Calculates the bounding box of the mesh with additional padding.
        /// </summary>
        private static AABB CalculateBoundsWithPadding(XRMesh mesh, float padding)
        {
            var bounds = mesh.Bounds;
            var paddingVector = new Vector3(padding);
            return new AABB(
                bounds.Min - paddingVector,
                bounds.Max + paddingVector
            );
        }

        /// <summary>
        /// Creates or recreates the SDF texture with the specified resolution.
        /// </summary>
        private void CreateSDFTexture(IVector3 resolution)
        {
            _sdfTexture?.Destroy();
            _sdfTexture = new XRTexture3D((uint)resolution.X, (uint)resolution.Y, (uint)resolution.Z) { Resizable = false };
        }

        /// <summary>
        /// Prepares the mesh data buffers for the compute shader.
        /// </summary>
        private void PrepareMeshBuffers(XRMesh mesh)
        {
            // Get vertex positions
            var positionsBuffer = mesh.Buffers[ECommonBufferType.Position.ToString()] ?? throw new InvalidOperationException("Mesh does not have position buffer");

            // Create vertices buffer for compute shader
            _verticesBuffer?.Destroy();
            _verticesBuffer = positionsBuffer.Clone(false, EBufferTarget.ShaderStorageBuffer);
            _verticesBuffer.AttributeName = "Vertices";
            _verticesBuffer.SetBlockIndex(1); // Binding 1 in the shader

            // Get triangle indices
            var indexBuffer = mesh.GetIndexBuffer(EPrimitiveType.Triangles, out _, EBufferTarget.ShaderStorageBuffer) ?? throw new InvalidOperationException("Mesh does not have index buffer");

            // Create indices buffer for compute shader
            _indicesBuffer?.Destroy();
            _indicesBuffer = indexBuffer;
            _indicesBuffer.AttributeName = "Indices";
            _indicesBuffer.SetBlockIndex(2); // Binding 2 in the shader

            // Optional: Prepare spatial acceleration buffers if enabled
            if (_useSpatialAcceleration)
            {
                PrepareSpatialAccelerationBuffers(mesh);
            }
        }

        /// <summary>
        /// Prepares spatial acceleration buffers for improved performance.
        /// </summary>
        private void PrepareSpatialAccelerationBuffers(XRMesh mesh)
        {
            // This is a simplified spatial acceleration structure
            // In a full implementation, you would build an octree or BVH
            
            var triangles = mesh.Triangles;
            if (triangles == null || triangles.Count == 0)
                return;

            // Create simple bounding spheres for each triangle
            var spatialNodes = new List<Vector4>();
            var triangleToNode = new List<uint>();

            for (int i = 0; i < triangles.Count; i++)
            {
                var tri = triangles[i];
                var pos0 = mesh.GetPosition((uint)tri.Point0);
                var pos1 = mesh.GetPosition((uint)tri.Point1);
                var pos2 = mesh.GetPosition((uint)tri.Point2);

                // Compute triangle center
                var center = (pos0 + pos1 + pos2) / 3.0f;
                
                // Compute bounding radius
                var radius = Math.Max(Math.Max(
                    Vector3.Distance(center, pos0),
                    Vector3.Distance(center, pos1)),
                    Vector3.Distance(center, pos2));

                spatialNodes.Add(new Vector4(center, radius));
                triangleToNode.Add((uint)i);
            }

            // Create spatial nodes buffer
            _spatialNodesBuffer?.Destroy();
            _spatialNodesBuffer = new XRDataBuffer("SpatialNodes", EBufferTarget.ShaderStorageBuffer, (uint)spatialNodes.Count, EComponentType.Float, 4, false, false);
            _spatialNodesBuffer.SetDataRaw(spatialNodes);
            _spatialNodesBuffer.SetBlockIndex(3); // Binding 3 in the shader

            // Create triangle to node mapping buffer
            _triangleToNodeBuffer?.Destroy();
            _triangleToNodeBuffer = new XRDataBuffer("TriangleToNode", EBufferTarget.ShaderStorageBuffer, (uint)triangleToNode.Count, EComponentType.UInt, 1, false, false);
            _triangleToNodeBuffer.SetDataRaw(triangleToNode);
            _triangleToNodeBuffer.SetBlockIndex(4); // Binding 4 in the shader
        }

        /// <summary>
        /// Executes the compute shader to generate the SDF.
        /// </summary>
        private void ExecuteComputeShader(AABB bounds, IVector3 resolution)
        {
            if (_computeProgram == null || _sdfTexture == null)
            {
                throw new InvalidOperationException("Compute program or SDF texture not initialized");
            }

            // Set uniform values
            _computeProgram.Uniform("sdfMinBounds", bounds.Min);
            _computeProgram.Uniform("sdfMaxBounds", bounds.Max);
            _computeProgram.Uniform("sdfResolution", resolution);
            _computeProgram.Uniform("maxDistance", _maxDistance);
            _computeProgram.Uniform("useSpatialAccel", _useSpatialAcceleration ? 1 : 0);
            _computeProgram.Uniform("epsilon", _epsilon);
            _computeProgram.Uniform("maxIterations", _maxIterations);

            // Bind the SDF texture as an image for writing
            _computeProgram.BindImageTexture(
                0, // Binding 0 in the shader
                _sdfTexture,
                0, // Mip level
                false, // Not layered
                0, // Layer
                XRRenderProgram.EImageAccess.WriteOnly,
                XRRenderProgram.EImageFormat.R32F
            );

            // Calculate dispatch dimensions
            // The shader uses local_size_x = 16, local_size_y = 8, local_size_z = 4
            uint groupSizeX = 16;
            uint groupSizeY = 8;
            uint groupSizeZ = 4;
            
            uint numGroupsX = (uint)((resolution.X + groupSizeX - 1) / groupSizeX);
            uint numGroupsY = (uint)((resolution.Y + groupSizeY - 1) / groupSizeY);
            uint numGroupsZ = (uint)((resolution.Z + groupSizeZ - 1) / groupSizeZ);

            // Dispatch the compute shader
            _computeProgram.DispatchCompute(numGroupsX, numGroupsY, numGroupsZ);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Cleanup();
            GC.SuppressFinalize(this);
        }

        #endregion
    }
} 