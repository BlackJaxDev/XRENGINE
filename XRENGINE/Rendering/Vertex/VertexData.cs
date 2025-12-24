using System.Numerics;

namespace XREngine.Data.Rendering
{
    public class VertexData : VertexPrimitive
    {
        public override FaceType Type => FaceType.Points;

        /// <summary>
        /// The position of the vertex. Required - will be 0,0,0 if not set.
        /// </summary>
        public Vector3 Position;

        /// <summary>
        /// The normal of the vertex. Affects lighting.
        /// </summary>
        public Vector3? Normal;

        /// <summary>
        /// The tangent of the vertex. Affects lighting.
        /// </summary>
        public Vector3? Tangent;

        //Bitangent is calculated from the normal and tangent on the GPU

        /// <summary>
        /// The texture coordinates of the vertex. Specifies how a texture is mapped to the vertex.
        /// This is a list because a mesh can have multiple texture coordinate sets.
        /// </summary>
        public List<Vector2>? TextureCoordinateSets { get; set; }

        /// <summary>
        /// The color of the vertex. Can be used by the shader for various effects.
        /// </summary>
        public List<Vector4>? ColorSets { get; set; }
    }
}