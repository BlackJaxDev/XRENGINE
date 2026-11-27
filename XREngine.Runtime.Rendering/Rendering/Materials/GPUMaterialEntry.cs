namespace XREngine.Rendering.Materials
{
    /// <summary>
    /// GPU material table entry. Texture fields are indices into <see cref="GPUMaterialTable.TextureHandleBuffer"/>,
    /// not API handles. This keeps the per-material row small and lets GL bindless handles or Vulkan descriptor
    /// indices share the same shader-facing indirection contract.
    /// </summary>
    public struct GPUMaterialEntry
    {
        /// <summary>
        /// The material's albedo alpha participates in hard coverage discard. Kept in an
        /// otherwise unused row-flag bit so the material-table ABI and stride remain stable.
        /// </summary>
        public const uint MaskedCoverageFlag = 1u << 3;

        /// <summary>The normal slot holds a grayscale height or bump map, not an RGB normal map.</summary>
        public const uint HeightMapFlag = 1u << 4;

        /// <summary>The height map drives parallax occlusion mapping.</summary>
        public const uint ParallaxFlag = 1u << 5;

        /// <summary>Parallax discards fragments whose ray leaves UV [0, 1].</summary>
        public const uint ParallaxSilhouetteFlag = 1u << 6;

        /// <summary>
        /// A separate opacity texture multiplies masked coverage by its red channel. Without
        /// this flag, coverage reads no opacity texture.
        /// </summary>
        public const uint OpacityTextureFlag = 1u << 7;

        /// <summary>
        /// The material converts masked coverage alpha into multisample sample coverage in the
        /// Advanced visibility raster. Single-sample targets fall back to the alpha cutoff test.
        /// </summary>
        public const uint AlphaToCoverageFlag = 1u << 28;

        /// <summary>
        /// The material has an ambient occlusion texture. Native shading samples it only when this
        /// flag is set, and multiplies the result into the screen-space ambient occlusion of indirect light.
        /// </summary>
        public const uint OcclusionTextureFlag = 1u << 29;

        /// <summary>
        /// The metallic-roughness texture uses the glTF packing: roughness in green and metallic in
        /// blue. Without this flag, native shading reads roughness from red and metallic from green.
        /// </summary>
        public const uint PackedMetallicRoughnessFlag = 1u << 30;

        /// <summary>Bits 0-1 of <see cref="OcclusionControls"/>: occlusion channel (0 red to 3 alpha).</summary>
        public const uint OcclusionChannelMask = 3u;

        /// <summary>Bit 2 of <see cref="OcclusionControls"/>: the occlusion texture reads texture-coordinate set 1.</summary>
        public const uint OcclusionUv1Flag = 1u << 2;

        /// <summary>Bit offsets of the packed parallax step counts (8 bits max, 8 bits min, 4 bits refine).</summary>
        public const int ParallaxMaxStepsShift = 8;
        public const int ParallaxMinStepsShift = 16;
        public const int ParallaxRefineStepsShift = 24;

        public uint AlbedoHandleIndex;
        public uint NormalHandleIndex;
        public uint RMHandleIndex;
        public uint Flags;

        /// <summary>Handle index of the separate opacity texture, or 0 when the material has none.</summary>
        public uint OpacityHandleIndex;

        /// <summary>Handle index of the ambient occlusion texture, or 0 when the material has none.</summary>
        public uint OcclusionHandleIndex;

        /// <summary>Strength of the ambient occlusion texture (glTF occlusion strength, 0 to 1).</summary>
        public float OcclusionStrength;

        /// <summary>
        /// Ambient occlusion texture controls: <see cref="OcclusionChannelMask"/> selects the channel and
        /// <see cref="OcclusionUv1Flag"/> selects texture-coordinate set 1.
        /// </summary>
        public uint OcclusionControls;
        public Vector4 BaseColorOpacity;
        public Vector4 RMSE;
        public float AlphaCutoff;

        /// <summary>Height-to-normal slope scale for a height or bump map.</summary>
        public float SurfaceDetailHeightScale;

        /// <summary>Parallax depth at full height range, in UV units.</summary>
        public float ParallaxScale;
    }
}
