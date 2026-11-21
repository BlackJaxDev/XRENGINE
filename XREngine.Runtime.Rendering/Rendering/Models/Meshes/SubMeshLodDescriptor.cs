using XREngine.Data.Geometry;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.Models
{
    /// <summary>
    /// The cooked description of one LOD of a <see cref="SubMesh"/>. The cook writes one descriptor for each LOD, so that a
    /// load can know the LOD table without the LOD payloads. A descriptor exists only on a sub-mesh that a cooked sub-mesh
    /// payload of codec version 2 gave; an editor sub-mesh and an older cooked sub-mesh have none.
    /// </summary>
    /// <remarks>
    /// A LOD that is not inline is a separate archive entry, <see cref="EntryName"/>, with a normal cooked
    /// <see cref="XRMesh"/> in it. The reader checks <see cref="PayloadLength"/> and <see cref="PayloadChecksum"/> before it
    /// parses the entry, and then checks <see cref="BoneSetHash"/> and <see cref="BlendshapeNameHash"/> against the mesh.
    /// </remarks>
    public sealed record SubMeshLodDescriptor
    {
        private ulong _contentKeyHigh;
        private ulong _contentKeyLow;

        /// <summary>The prefix of the archive entry name of a LOD payload.</summary>
        public const string EntryPrefix = "lod/";

        /// <summary>The flags of the LOD.</summary>
        public ESubMeshLodFlags Flags { get; init; }

        /// <summary>True if the payload is inline in the cooked sub-mesh.</summary>
        public bool IsInline => (Flags & ESubMeshLodFlags.IsInline) != 0;

        /// <summary>The bind-pose bounds of the LOD mesh at cook time.</summary>
        public AABB Bounds { get; init; }

        /// <summary>The vertex count of the LOD mesh.</summary>
        public uint VertexCount { get; init; }

        /// <summary>The index count of the LOD mesh.</summary>
        public uint IndexCount { get; init; }

        /// <summary>The bone count of the LOD mesh.</summary>
        public uint BoneCount { get; init; }

        /// <summary>The blendshape count of the LOD mesh.</summary>
        public uint BlendshapeCount { get; init; }

        /// <summary>The hash of the ordered bone references and inverse bind matrices. See <see cref="SubMeshLodHashes"/>.</summary>
        public ulong BoneSetHash { get; init; }

        /// <summary>The hash of the ordered blendshape names. See <see cref="SubMeshLodHashes"/>.</summary>
        public ulong BlendshapeNameHash { get; init; }

        /// <summary>The content key of the payload: the first 128 bits of a SHA-256 hash. Zero for the inline LOD.</summary>
        [YamlIgnore]
        public UInt128 ContentKey
        {
            get => new(_contentKeyHigh, _contentKeyLow);
            init
            {
                _contentKeyHigh = (ulong)(value >> 64);
                _contentKeyLow = (ulong)value;
            }
        }

        /// <summary>The high 64 bits of <see cref="ContentKey"/>, for the generic cooked serializer.</summary>
        private ulong CookedContentKeyHigh
        {
            get => _contentKeyHigh;
            set => _contentKeyHigh = value;
        }

        /// <summary>The low 64 bits of <see cref="ContentKey"/>, for the generic cooked serializer.</summary>
        private ulong CookedContentKeyLow
        {
            get => _contentKeyLow;
            set => _contentKeyLow = value;
        }

        /// <summary>The length of the payload bytes.</summary>
        public ulong PayloadLength { get; init; }

        /// <summary>The xxHash3-64 of the payload bytes.</summary>
        public ulong PayloadChecksum { get; init; }

        /// <summary>The version of the mesh payload format. See <see cref="SubMeshCookedCodec.MeshCodecVersion"/>.</summary>
        public uint MeshCodecVersion { get; init; }

        /// <summary>The archive entry name of the payload: <c>lod/</c> and 32 lowercase hex digits of the key.</summary>
        public string EntryName => GetEntryName(ContentKey);

        /// <summary>Gets the archive entry name of a content key.</summary>
        /// <param name="contentKey">The content key.</param>
        public static string GetEntryName(UInt128 contentKey)
            => EntryPrefix + contentKey.ToString("x32");
    }
}
