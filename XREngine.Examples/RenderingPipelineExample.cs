using System;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;

namespace XREngine.Examples
{
    /// <summary>
    /// Example showing how to use both traditional and meshlet rendering pipelines
    /// </summary>
    public class RenderingPipelineExample
    {
        private HybridRenderingManager? _renderingManager;
        private OpenGLRenderer? _renderer;

        public void Initialize(OpenGLRenderer renderer)
        {
            _renderer = renderer;
            _renderingManager = new HybridRenderingManager(renderer);

            // Check capabilities and choose pipeline
            if (_renderingManager.IsMeshShaderSupported())
            {
                Console.WriteLine("Mesh shaders supported! Using meshlet pipeline.");
                _renderingManager.SetRenderingPipeline(true);
            }
            else
            {
                Console.WriteLine("Mesh shaders not supported. Using traditional pipeline.");
                _renderingManager.SetRenderingPipeline(false);
            }

            SetupScene();
        }

        private void SetupScene()
        {
            if (_renderingManager == null)
                return;

            // Add some materials
            _renderingManager.AddMaterial(0, 
                albedo: new Vector4(0.7f, 0.3f, 0.3f, 1.0f),    // Red-ish
                metallic: 0.0f,
                roughness: 0.8f,
                ao: 1.0f);

            _renderingManager.AddMaterial(1,
                albedo: new Vector4(0.3f, 0.7f, 0.3f, 1.0f),    // Green-ish
                metallic: 0.2f,
                roughness: 0.4f,
                ao: 1.0f);

            _renderingManager.AddMaterial(2,
                albedo: new Vector4(0.3f, 0.3f, 0.7f, 1.0f),    // Blue-ish
                metallic: 0.8f,
                roughness: 0.2f,
                ao: 1.0f);

            // Create some example meshes
            var cubeMesh = CreateCubeMesh();
            var sphereMesh = CreateSphereMesh();

            // Register meshes
            _renderingManager.RegisterMesh(0, cubeMesh, 0, Matrix4.CreateTranslation(-2, 0, 0));
            _renderingManager.RegisterMesh(1, sphereMesh, 1, Matrix4.CreateTranslation(0, 0, 0));
            _renderingManager.RegisterMesh(2, cubeMesh, 2, Matrix4.CreateTranslation(2, 0, 0));
        }

        public void Render(Matrix4 viewMatrix, Matrix4 projectionMatrix, Vector3 cameraPosition)
        {
            if (_renderingManager == null)
                return;

            var viewProjectionMatrix = viewMatrix * projectionMatrix;

            // Calculate frustum planes (simplified - in practice use proper frustum extraction)
            var frustumPlanes = new Vector4[6];
            ExtractFrustumPlanes(viewProjectionMatrix, frustumPlanes);

            // Lighting parameters
            var lightDirection = Vector3.Normalize(new Vector3(-1, -1, -1));
            var lightColor = new Vector3(1.0f, 1.0f, 0.9f);
            var lightIntensity = 3.0f;

            // For traditional pipeline, you'd also need culled commands and indirect draw buffers
            GLDataBuffer? culledCommandsBuffer = null;
            GLDataBuffer? indirectDrawBuffer = null;
            int currentRenderPass = 0;

            _renderingManager.Render(
                viewProjectionMatrix, viewMatrix, cameraPosition,
                frustumPlanes, lightDirection, lightColor, lightIntensity,
                culledCommandsBuffer!, indirectDrawBuffer!, currentRenderPass);
        }

        public void UpdateAnimation(float deltaTime)
        {
            if (_renderingManager == null)
                return;

            // Animate mesh transforms
            float time = Environment.TickCount * 0.001f;
            
            _renderingManager.UpdateMeshTransform(0, 
                Matrix4.CreateRotationY(time) * Matrix4.CreateTranslation(-2, 0, 0));
            
            _renderingManager.UpdateMeshTransform(1,
                Matrix4.CreateRotationX(time * 0.7f) * Matrix4.CreateTranslation(0, 0, 0));
            
            _renderingManager.UpdateMeshTransform(2,
                Matrix4.CreateRotationZ(time * 1.3f) * Matrix4.CreateTranslation(2, 0, 0));
        }

        public void ToggleRenderingPipeline()
        {
            if (_renderingManager == null)
                return;

            bool currentMode = _renderingManager.UseMeshletPipeline;
            bool newMode = !currentMode;

            if (newMode && !_renderingManager.IsMeshShaderSupported())
            {
                Console.WriteLine("Cannot switch to meshlet pipeline - mesh shaders not supported!");
                return;
            }

            _renderingManager.SetRenderingPipeline(newMode);
            
            var stats = _renderingManager.GetStats();
            Console.WriteLine($"Switched to {(newMode ? "meshlet" : "traditional")} pipeline. " +
                            $"Rendering {stats.MeshCount} meshes.");
        }

        private XRMesh CreateCubeMesh()
        {
            // Simple cube mesh creation
            var positions = new Vector3[]
            {
                // Front face
                new(-1, -1,  1), new( 1, -1,  1), new( 1,  1,  1), new(-1,  1,  1),
                // Back face
                new(-1, -1, -1), new(-1,  1, -1), new( 1,  1, -1), new( 1, -1, -1),
                // Top face
                new(-1,  1, -1), new(-1,  1,  1), new( 1,  1,  1), new( 1,  1, -1),
                // Bottom face
                new(-1, -1, -1), new( 1, -1, -1), new( 1, -1,  1), new(-1, -1,  1),
                // Right face
                new( 1, -1, -1), new( 1,  1, -1), new( 1,  1,  1), new( 1, -1,  1),
                // Left face
                new(-1, -1, -1), new(-1, -1,  1), new(-1,  1,  1), new(-1,  1, -1)
            };

            var triangles = new Triangle[]
            {
                // Front
                new(0, 1, 2), new(2, 3, 0),
                // Back
                new(4, 5, 6), new(6, 7, 4),
                // Top
                new(8, 9, 10), new(10, 11, 8),
                // Bottom
                new(12, 13, 14), new(14, 15, 12),
                // Right
                new(16, 17, 18), new(18, 19, 16),
                // Left
                new(20, 21, 22), new(22, 23, 20)
            };

            return new XRMesh
            {
                Positions = positions,
                Triangles = triangles,
                // Normals, UVs, etc. would be calculated or provided
            };
        }

        private XRMesh CreateSphereMesh()
        {
            // Simplified sphere mesh creation
            var positions = new List<Vector3>();
            var triangles = new List<Triangle>();

            int segments = 16;
            int rings = 8;

            // Generate vertices
            for (int ring = 0; ring <= rings; ring++)
            {
                float phi = (float)(Math.PI * ring / rings);
                for (int segment = 0; segment <= segments; segment++)
                {
                    float theta = (float)(2.0 * Math.PI * segment / segments);
                    
                    float x = (float)(Math.Sin(phi) * Math.Cos(theta));
                    float y = (float)(Math.Cos(phi));
                    float z = (float)(Math.Sin(phi) * Math.Sin(theta));
                    
                    positions.Add(new Vector3(x, y, z));
                }
            }

            // Generate triangles
            for (int ring = 0; ring < rings; ring++)
            {
                for (int segment = 0; segment < segments; segment++)
                {
                    int current = ring * (segments + 1) + segment;
                    int next = current + segments + 1;

                    triangles.Add(new Triangle(current, next, current + 1));
                    triangles.Add(new Triangle(current + 1, next, next + 1));
                }
            }

            return new XRMesh
            {
                Positions = positions.ToArray(),
                Triangles = triangles.ToArray(),
            };
        }

        private void ExtractFrustumPlanes(Matrix4 viewProjection, Vector4[] planes)
        {
            // Simplified frustum plane extraction
            // In practice, you'd extract planes from the view-projection matrix
            for (int i = 0; i < 6; i++)
                planes[i] = new Vector4(1, 0, 0, 100); // Dummy values
        }

        public void Dispose()
        {
            _renderingManager?.Dispose();
        }
    }
}
