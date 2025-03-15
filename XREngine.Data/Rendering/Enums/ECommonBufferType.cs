namespace XREngine.Data.Rendering
{
    public enum ECommonBufferType
    {
        /// <summary>
        /// Use this for uncommon buffer types.
        /// </summary>
        Unspecified = 0,

        /// <summary>
        /// 3D coordinates for the location of each vertex.
        /// </summary>
        Position,
        /// <summary>
        /// 3D normals to calculate lighting for each vertex.
        /// </summary>
        Normal,
        /// <summary>
        /// 3D tangents to calculate lighting for each vertex.
        /// </summary>
        Tangent,
        /// <summary>
        /// Color values for each vertex.
        /// </summary>
        Color,
        /// <summary>
        /// Texture coordinates to align textures for each vertex.
        /// </summary>
        TexCoord,

        /// <summary>
        /// The user-set weight of each blendshape.
        /// </summary>
        BlendshapeWeights,
        /// <summary>
        /// The index into blendshape indices, and the number of blendshapes affecting this particular vertex (pos, norm or tan has a non-zero delta).
        /// </summary>
        BlendshapeCount,
        /// <summary>
        /// Array of vec4s containing the indices into the blendshape deltas buffer.
        /// Each vertex has an arbitrary number of vec4s, one for each blendshape affecting it.
        /// vec4: blendshape index, pos delta index, norm delta index, tan delta index
        /// </summary>
        BlendshapeIndices,
        /// <summary>
        /// Remapped array of all position, normal, and tangent offsets.
        /// Referred to by the blendshape indices buffer.
        /// </summary>
        BlendshapeDeltas,

        /// <summary>
        /// The offset into the indices/weights array for each vertex.
        /// Add indices up until count to retrieve all bone indices/weights for a vertex.
        /// </summary>
        BoneMatrixOffset,
        /// <summary>
        /// The number of bones affecting the postion of each vertex.
        /// </summary>
        BoneMatrixCount,
        /// <summary>
        /// The weight of each bone affecting the position of each vertex.
        /// </summary>
        BoneMatrixWeights,
        /// <summary>
        /// The index into the bone matrix buffer for each bone affecting each vertex.
        /// </summary>
        BoneMatrixIndices,

        /// <summary>
        /// The animated world matrices for each bone utilized by the mesh.
        /// The first matrix is identity.
        /// </summary>
        BoneMatrices,
        /// <summary>
        /// The bind pose inverse world matrices for each bone utilized by the mesh.
        /// The first matrix is identity.
        /// </summary>
        BoneInvBindMatrices,

        GlyphTransforms,
        GlyphTexCoords,

        InterleavedVertex,
    }
}
