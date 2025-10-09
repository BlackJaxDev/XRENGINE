using System.Numerics;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.Compute
{
    /// <summary>
    /// Example class demonstrating how to use the MeshSDFGenerator to generate
    /// Signed Distance Fields from mesh data.
    /// </summary>
    public static class MeshSDFExample
    {
        /// <summary>
        /// Example method showing how to generate an SDF from a mesh with automatic bounds calculation.
        /// </summary>
        /// <param name="mesh">The mesh to generate SDF from</param>
        /// <returns>The generated SDF texture, or null if generation failed</returns>
        public static XRTexture3D? GenerateSDFFromMesh(XRMesh mesh)
        {
            // Create the SDF generator
            using var sdfGenerator = new MeshSDFGenerator();
            
            // Initialize the generator
            sdfGenerator.Initialize();
            
            // Define the resolution for the 3D SDF texture
            // Higher resolution = more accurate SDF but more memory usage
            var resolution = new IVector3(128, 128, 128);
            
            // Generate the SDF with automatic bounds calculation and 10% padding
            var sdfTexture = sdfGenerator.GenerateSDF(mesh, resolution, padding: 0.1f);
            
            if (sdfTexture != null)
            {
                Debug.Out($"Successfully generated SDF with resolution {resolution}");
            }
            else
            {
                Debug.LogWarning("Failed to generate SDF");
            }
            
            return sdfTexture;
        }

        /// <summary>
        /// Example method showing how to generate an SDF from a mesh with custom bounds.
        /// </summary>
        /// <param name="mesh">The mesh to generate SDF from</param>
        /// <param name="minBounds">Minimum bounds of the SDF volume</param>
        /// <param name="maxBounds">Maximum bounds of the SDF volume</param>
        /// <returns>The generated SDF texture, or null if generation failed</returns>
        public static XRTexture3D? GenerateSDFWithCustomBounds(XRMesh mesh, Vector3 minBounds, Vector3 maxBounds)
        {
            // Create the SDF generator
            using var sdfGenerator = new MeshSDFGenerator();
            
            // Initialize the generator
            sdfGenerator.Initialize();
            
            // Define the resolution for the 3D SDF texture
            var resolution = new IVector3(256, 256, 256);
            
            // Generate the SDF with custom bounds
            var sdfTexture = sdfGenerator.GenerateSDF(mesh, resolution, minBounds, maxBounds);
            
            if (sdfTexture != null)
                Debug.Out($"Successfully generated SDF with custom bounds and resolution {resolution}");
            else
                Debug.LogWarning("Failed to generate SDF with custom bounds");
                        
            return sdfTexture;
        }

        /// <summary>
        /// Example method showing how to generate a high-resolution SDF for detailed collision detection.
        /// </summary>
        /// <param name="mesh">The mesh to generate SDF from</param>
        /// <returns>The generated SDF texture, or null if generation failed</returns>
        public static XRTexture3D? GenerateHighResolutionSDF(XRMesh mesh)
        {
            // Create the SDF generator
            using var sdfGenerator = new MeshSDFGenerator();
            
            // Initialize the generator
            sdfGenerator.Initialize();
            
            // Use high resolution for detailed SDF
            var resolution = new IVector3(512, 512, 512);
            
            // Generate the SDF with minimal padding for precise collision detection
            var sdfTexture = sdfGenerator.GenerateSDF(mesh, resolution, padding: 0.05f);
            
            if (sdfTexture != null)
                Debug.Out($"Successfully generated high-resolution SDF with resolution {resolution}");
            else
                Debug.LogWarning("Failed to generate high-resolution SDF");
                        
            return sdfTexture;
        }

        /// <summary>
        /// Example method showing how to generate multiple SDFs with different resolutions.
        /// Useful for LOD (Level of Detail) systems.
        /// </summary>
        /// <param name="mesh">The mesh to generate SDFs from</param>
        /// <returns>Array of SDF textures with different resolutions</returns>
        public static XRTexture3D[] GenerateMultiResolutionSDFs(XRMesh mesh)
        {
            var sdfTextures = new XRTexture3D[3];
            
            // Create the SDF generator
            using var sdfGenerator = new MeshSDFGenerator();
            
            // Initialize the generator
            sdfGenerator.Initialize();
            
            // Generate SDFs at different resolutions
            var resolutions = new[]
            {
                new IVector3(64, 64, 64),   // Low resolution for distant objects
                new IVector3(128, 128, 128), // Medium resolution for normal distance
                new IVector3(256, 256, 256)  // High resolution for close objects
            };
            
            for (int i = 0; i < resolutions.Length; i++)
            {
                var resolution = resolutions[i];
                var sdfTexture = sdfGenerator.GenerateSDF(mesh, resolution, padding: 0.1f);
                
                if (sdfTexture != null)
                {
                    sdfTextures[i] = sdfTexture;
                    Debug.Out($"Generated SDF {i + 1} with resolution {resolution}");
                }
                else
                    Debug.LogWarning($"Failed to generate SDF {i + 1} with resolution {resolution}");
            }
            
            return sdfTextures;
        }

        /// <summary>
        /// Example method showing how to use the SDF generator in a component or system.
        /// </summary>
        public static void ExampleUsageInComponent()
        {
            // This is an example of how you might use the SDF generator in a component
            
            // 1. Create and initialize the generator (do this once, perhaps in OnComponentActivated)
            var sdfGenerator = new MeshSDFGenerator();
            sdfGenerator.Initialize();
            
            // 2. When you need to generate an SDF (e.g., when a mesh changes)
            // var mesh = GetMeshFromSomewhere();
            // var sdfTexture = sdfGenerator.GenerateSDF(mesh, new IVector3(128, 128, 128));
            
            // 3. Use the SDF texture for collision detection, rendering, etc.
            // if (sdfTexture != null)
            // {
            //     // Store the SDF texture or use it for calculations
            //     // You can sample the SDF texture in shaders for collision detection
            // }
            
            // 4. Clean up when done (do this in OnComponentDeactivated or Dispose)
            sdfGenerator.Cleanup();
        }
    }
} 