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
        public Vector4 BaseColorOpacity;
        public Vector4 RMSE;
        public float AlphaCutoff;

        /// <summary>Height-to-normal slope scale for a height or bump map.</summary>
        public float SurfaceDetailHeightScale;

        /// <summary>Parallax depth at full height range, in UV units.</summary>
        public float ParallaxScale;
    }
}
