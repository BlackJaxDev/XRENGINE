using System.IO;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Physics.DebugVisualization
{
    /// <summary>
    /// Vertex-expanded instanced debug primitives. Each instance draws a small scaffold mesh
    /// and the vertex stage reads the instance data from the primitive storage buffer, so no
    /// geometry stage is needed. Every material gets a single-view vertex shader and an
    /// OVR multiview stereo variant; the mesh renderer selects the variant for the pass.
    /// </summary>
    internal static class DebugPrimitiveShaders
    {
        /// <summary>Loads the single-view and stereo vertex shaders for one primitive kind.</summary>
        /// <param name="primitive">Point, Line or Triangle.</param>
        /// <param name="compressed">True for the packed instance layout.</param>
        internal static XRShader[] LoadVertexShaders(string primitive, bool compressed)
        {
            string baseName = $"Debug{primitive}Instance{(compressed ? "Compressed" : string.Empty)}";
            return
            [
                ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", baseName + ".vs"), EShaderType.Vertex),
                ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "vs", baseName + "Stereo.vs"), EShaderType.Vertex),
            ];
        }

        /// <summary>Two-triangle quad with corners at -1/+1 in x and y, for points and lines.</summary>
        internal static XRMesh CreateQuadScaffold()
            => new(
                [new Vertex(new Vector3(-1, -1, 0)), new Vertex(new Vector3(1, -1, 0)),
                 new Vertex(new Vector3(-1, 1, 0)), new Vertex(new Vector3(1, 1, 0))],
                [0, 1, 2, 2, 1, 3]);

        /// <summary>One triangle whose x coordinate selects the instance corner (0, 1, 2).</summary>
        internal static XRMesh CreateTriangleScaffold()
            => new(
                [new Vertex(Vector3.Zero), new Vertex(Vector3.UnitX), new Vertex(2 * Vector3.UnitX)],
                [0, 1, 2]);
    }
}
