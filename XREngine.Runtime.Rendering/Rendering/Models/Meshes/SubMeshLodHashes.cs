using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace XREngine.Rendering.Models
{
    /// <summary>
    /// The hashes of the LOD descriptors of a cooked <see cref="SubMesh"/>. The cook and the reader use the same functions,
    /// so that the reader can find a LOD payload that does not agree with its descriptor.
    /// </summary>
    public static class SubMeshLodHashes
    {
        /// <summary>
        /// Gets the hash of the ordered bones of a mesh: the serialized reference ID of each bone and its inverse bind matrix.
        /// A mesh with no bones gives zero.
        /// </summary>
        /// <param name="mesh">The mesh.</param>
        public static ulong ComputeBoneSetHash(XRMesh mesh)
        {
            ArgumentNullException.ThrowIfNull(mesh);
            var bones = mesh.UtilizedBones;
            if (bones is not { Length: > 0 })
                return 0UL;

            XxHash3 hash = new();
            Span<byte> buffer = stackalloc byte[16 + (16 * sizeof(float))];
            for (int i = 0; i < bones.Length; i++)
            {
                (var bone, Matrix4x4 inverseBind) = bones[i];
                Guid id = bone?.EffectiveSerializedReferenceId ?? Guid.Empty;
                id.TryWriteBytes(buffer[..16]);
                MemoryMarshal.Write(buffer[16..], in inverseBind);
                hash.Append(buffer);
            }
            return hash.GetCurrentHashAsUInt64();
        }

        /// <summary>Gets the hash of the ordered blendshape names of a mesh. A mesh with no blendshapes gives zero.</summary>
        /// <param name="mesh">The mesh.</param>
        public static ulong ComputeBlendshapeNameHash(XRMesh mesh)
        {
            ArgumentNullException.ThrowIfNull(mesh);
            string[]? names = mesh.BlendshapeNames;
            if (names is not { Length: > 0 })
                return 0UL;

            XxHash3 hash = new();
            Span<byte> separator = [0];
            for (int i = 0; i < names.Length; i++)
            {
                hash.Append(Encoding.UTF8.GetBytes(names[i] ?? string.Empty));
                hash.Append(separator);
            }
            return hash.GetCurrentHashAsUInt64();
        }

        /// <summary>Gets the xxHash3-64 checksum of payload bytes.</summary>
        /// <param name="payload">The payload bytes.</param>
        public static ulong ComputePayloadChecksum(ReadOnlySpan<byte> payload)
            => XxHash3.HashToUInt64(payload);

        /// <summary>
        /// Gets the content key of a LOD payload: the first 128 bits of SHA-256 over the mesh codec version (32-bit little
        /// endian) and the payload bytes. Only the cook computes it; the runtime checks the checksum.
        /// </summary>
        /// <param name="meshCodecVersion">The version of the mesh payload format.</param>
        /// <param name="payload">The payload bytes.</param>
        public static UInt128 ComputeContentKey(uint meshCodecVersion, ReadOnlySpan<byte> payload)
        {
            using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> version = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(version, meshCodecVersion);
            sha.AppendData(version);
            sha.AppendData(payload);
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            sha.GetHashAndReset(digest);
            return BinaryPrimitives.ReadUInt128BigEndian(digest[..16]);
        }
    }
}
