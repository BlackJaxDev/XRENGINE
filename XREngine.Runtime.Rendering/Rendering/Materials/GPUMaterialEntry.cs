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

        public uint AlbedoHandleIndex;
        public uint NormalHandleIndex;
        public uint RMHandleIndex;
        public uint Flags;
        public Vector4 BaseColorOpacity;
        public Vector4 RMSE;
        public float AlphaCutoff;
    }
}
