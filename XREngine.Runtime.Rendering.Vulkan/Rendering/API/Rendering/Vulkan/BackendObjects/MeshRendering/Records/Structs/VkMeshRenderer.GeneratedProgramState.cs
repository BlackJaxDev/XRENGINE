using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkMeshRenderer
    {
        /// <summary>
        /// Allocation-free snapshot of every input that changes a generated Vulkan mesh program.
        /// Reference identity is intentional for immutable source/name strings so clean-frame
        /// lookups never re-hash large shader source text.
        /// </summary>
        private readonly struct GeneratedProgramState : IEquatable<GeneratedProgramState>
        {
            public GeneratedProgramState(
                XRMaterial material,
                long shaderStateRevision,
                ulong shaderSourceSignature,
                ulong materialVariantHash,
                bool materialVariantIsEmpty,
                string? generatedVertexSource,
                string? materialName,
                string? meshName,
                string? rendererName,
                string versionKindLabel,
                EProgramPriority programPriority,
                int shaderConfigVersion,
                bool hasSkinning,
                bool useComputeSkinning,
                bool hasBlendshapes,
                bool useComputeBlendshapes,
                bool usePrecombinedBlendshapes,
                bool meshDeformEnabled,
                EDirectionalCascadeShadowMaterialKind directionalShadowKind,
                EPointShadowMaterialKind pointShadowKind,
                bool useDepthNormalVariants,
                ERenderClipDepthRange clipDepthRange,
                ERenderClipSpaceYDirection clipYDirection)
            {
                Material = material;
                ShaderStateRevision = shaderStateRevision;
                ShaderSourceSignature = shaderSourceSignature;
                MaterialVariantHash = materialVariantHash;
                MaterialVariantIsEmpty = materialVariantIsEmpty;
                GeneratedVertexSource = generatedVertexSource;
                MaterialName = materialName;
                MeshName = meshName;
                RendererName = rendererName;
                VersionKindLabel = versionKindLabel;
                ProgramPriority = programPriority;
                ShaderConfigVersion = shaderConfigVersion;
                HasSkinning = hasSkinning;
                UseComputeSkinning = useComputeSkinning;
                HasBlendshapes = hasBlendshapes;
                UseComputeBlendshapes = useComputeBlendshapes;
                UsePrecombinedBlendshapes = usePrecombinedBlendshapes;
                MeshDeformEnabled = meshDeformEnabled;
                DirectionalShadowKind = directionalShadowKind;
                PointShadowKind = pointShadowKind;
                UseDepthNormalVariants = useDepthNormalVariants;
                ClipDepthRange = clipDepthRange;
                ClipYDirection = clipYDirection;
            }

            public XRMaterial Material { get; }
            public long ShaderStateRevision { get; }
            public ulong ShaderSourceSignature { get; }
            public ulong MaterialVariantHash { get; }
            public bool MaterialVariantIsEmpty { get; }
            public string? GeneratedVertexSource { get; }
            public string? MaterialName { get; }
            public string? MeshName { get; }
            public string? RendererName { get; }
            public string VersionKindLabel { get; }
            public EProgramPriority ProgramPriority { get; }
            public int ShaderConfigVersion { get; }
            public bool HasSkinning { get; }
            public bool UseComputeSkinning { get; }
            public bool HasBlendshapes { get; }
            public bool UseComputeBlendshapes { get; }
            public bool UsePrecombinedBlendshapes { get; }
            public bool MeshDeformEnabled { get; }
            public EDirectionalCascadeShadowMaterialKind DirectionalShadowKind { get; }
            public EPointShadowMaterialKind PointShadowKind { get; }
            public bool UseDepthNormalVariants { get; }
            public ERenderClipDepthRange ClipDepthRange { get; }
            public ERenderClipSpaceYDirection ClipYDirection { get; }

            public bool Equals(GeneratedProgramState other)
                => ReferenceEquals(Material, other.Material) &&
                   ShaderStateRevision == other.ShaderStateRevision &&
                   ShaderSourceSignature == other.ShaderSourceSignature &&
                   MaterialVariantHash == other.MaterialVariantHash &&
                   MaterialVariantIsEmpty == other.MaterialVariantIsEmpty &&
                   ReferenceEquals(GeneratedVertexSource, other.GeneratedVertexSource) &&
                   ReferenceEquals(MaterialName, other.MaterialName) &&
                   ReferenceEquals(MeshName, other.MeshName) &&
                   ReferenceEquals(RendererName, other.RendererName) &&
                   ReferenceEquals(VersionKindLabel, other.VersionKindLabel) &&
                   ProgramPriority == other.ProgramPriority &&
                   ShaderConfigVersion == other.ShaderConfigVersion &&
                   HasSkinning == other.HasSkinning &&
                   UseComputeSkinning == other.UseComputeSkinning &&
                   HasBlendshapes == other.HasBlendshapes &&
                   UseComputeBlendshapes == other.UseComputeBlendshapes &&
                   UsePrecombinedBlendshapes == other.UsePrecombinedBlendshapes &&
                   MeshDeformEnabled == other.MeshDeformEnabled &&
                   DirectionalShadowKind == other.DirectionalShadowKind &&
                   PointShadowKind == other.PointShadowKind &&
                   UseDepthNormalVariants == other.UseDepthNormalVariants &&
                   ClipDepthRange == other.ClipDepthRange &&
                   ClipYDirection == other.ClipYDirection;

            public override bool Equals(object? obj)
                => obj is GeneratedProgramState other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    Add(ref hash, ReferenceHash(Material));
                    Add(ref hash, ShaderStateRevision);
                    Add(ref hash, ShaderSourceSignature);
                    Add(ref hash, MaterialVariantHash);
                    Add(ref hash, MaterialVariantIsEmpty);
                    Add(ref hash, ReferenceHash(GeneratedVertexSource));
                    Add(ref hash, ReferenceHash(MaterialName));
                    Add(ref hash, ReferenceHash(MeshName));
                    Add(ref hash, ReferenceHash(RendererName));
                    Add(ref hash, ReferenceHash(VersionKindLabel));
                    Add(ref hash, (int)ProgramPriority);
                    Add(ref hash, ShaderConfigVersion);
                    Add(ref hash, HasSkinning);
                    Add(ref hash, UseComputeSkinning);
                    Add(ref hash, HasBlendshapes);
                    Add(ref hash, UseComputeBlendshapes);
                    Add(ref hash, UsePrecombinedBlendshapes);
                    Add(ref hash, MeshDeformEnabled);
                    Add(ref hash, (int)DirectionalShadowKind);
                    Add(ref hash, (int)PointShadowKind);
                    Add(ref hash, UseDepthNormalVariants);
                    Add(ref hash, (int)ClipDepthRange);
                    Add(ref hash, (int)ClipYDirection);
                    return hash;
                }
            }

            private static int ReferenceHash(object? value)
                => value is null ? 0 : RuntimeHelpers.GetHashCode(value);

            private static void Add(ref int hash, bool value)
                => hash = (hash * 31) + (value ? 1 : 0);

            private static void Add(ref int hash, int value)
                => hash = (hash * 31) + value;

            private static void Add(ref int hash, long value)
                => Add(ref hash, unchecked((int)(value ^ (value >> 32))));

            private static void Add(ref int hash, ulong value)
                => Add(ref hash, unchecked((int)(value ^ (value >> 32))));
        }
    }
}
