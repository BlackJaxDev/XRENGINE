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
        /// Four compact core bone indices per vertex for compressed skinning.
        /// </summary>
        BoneInfluenceCoreIndices,
        /// <summary>
        /// Four normalized compact core bone weights per vertex for compressed skinning.
        /// </summary>
        BoneInfluenceCoreWeights,
        /// <summary>
        /// One packed spill header per vertex for compressed skinning.
        /// </summary>
        BoneInfluenceSpillHeaders,
        /// <summary>
        /// Packed extra influence entries for vertices with more than four retained influences.
        /// </summary>
        BoneInfluenceSpillEntries,

        /// <summary>
        /// The animated world matrices for each bone utilized by the mesh.
        /// The first matrix is identity.
        /// Legacy source stream retained for systems that still publish bone matrices.
        /// </summary>
        BoneMatrices,
        /// <summary>
        /// The bind pose inverse world matrices for each bone utilized by the mesh.
        /// The first matrix is identity.
        /// Legacy source stream retained for systems that still publish inverse bind matrices.
        /// </summary>
        BoneInvBindMatrices,
        /// <summary>
        /// Final affine skin matrices, packed as three vec4 rows per bone.
        /// The first record is identity.
        /// </summary>
        SkinPalette,

        GlyphTransforms,
        GlyphTexCoords,

        InterleavedVertex,
        IndirectDraw,
    }
}
