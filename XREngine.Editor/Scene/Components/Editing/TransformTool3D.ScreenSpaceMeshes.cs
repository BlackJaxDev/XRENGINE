using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Scene.Components.Editing;

public partial class TransformTool3D
{
    /// <summary>
    /// Builds immutable segment triangles once. Position and Color0.xyz carry the
    /// two endpoints; Color0.w selects the expanded corner in the vertex shader.
    /// This keeps fixed-pixel-width gizmos on the GPU without a geometry stage,
    /// including OVR devices without multiview geometry-shader support.
    /// </summary>
    private static XRMesh CreateGizmoLineMesh(bool arrowHead, VertexLinePrimitive primitive)
        => CreateGizmoLineMesh(arrowHead, primitive.ToLines());

    private static XRMesh CreateGizmoLineMesh(bool arrowHead, params VertexLine[] lines)
    {
        List<VertexTriangle> triangles = [];
        foreach (VertexLine line in lines)
        {
            Vector3 start = line.Vertex0.Position;
            Vector3 end = line.Vertex1.Position;
            Vertex Corner(int index)
            {
                bool atEnd = arrowHead ? index == 0 : index >= 2;
                return new Vertex(atEnd ? end : start, new Vector4(atEnd ? start : end, index));
            }

            Vertex a = Corner(0);
            Vertex b = Corner(1);
            Vertex c = Corner(2);
            triangles.Add(new VertexTriangle(a, b, c));
            if (!arrowHead)
                triangles.Add(new VertexTriangle(c, b, Corner(3)));
        }
        return XRMesh.Create(triangles);
    }

    private static XRShader[] LoadGizmoShaders(string vertexName, string fragmentName)
        =>
        [
            ShaderHelper.LoadEngineShader($"Common/{vertexName}.vs", EShaderType.Vertex),
            ShaderHelper.LoadEngineShader($"Common/{vertexName}Stereo.vs", EShaderType.Vertex),
            ShaderHelper.LoadEngineShader($"Common/{vertexName}StereoNV.vs", EShaderType.Vertex),
            ShaderHelper.LoadEngineShader($"Common/{fragmentName}.fs", EShaderType.Fragment),
        ];
}
