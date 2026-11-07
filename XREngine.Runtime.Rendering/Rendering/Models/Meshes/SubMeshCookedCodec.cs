using System.Buffers;
using System.Numerics;
using System.Text;
using XREngine.Core.Files;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;
using GenericCookedBinarySerializer = XREngine.Core.Files.CookedBinarySerializer;

namespace XREngine.Rendering.Models
{
    /// <summary>
    /// The registered cooked codec of <see cref="SubMesh"/>. The payload (codec version 2) holds the sub-mesh fields, one
    /// descriptor for each LOD, and the payload of the coarsest LOD. Each other LOD is a separate entry
    /// <c>lod/&lt;key&gt;</c> with a normal cooked <see cref="XRMesh"/> (format <see cref="CookedAssetFormat.RuntimeBinaryV1"/>).
    /// </summary>
    /// <remarks>
    /// <para>Payload layout, little endian:</para>
    /// <code>
    /// u32     CodecVersion = 2
    /// asset   ID (16 bytes), Name, OriginalPath (nullable strings)
    /// fields  ImportedEntityIdentity, ImportedEntityIdentityIsStable, Bounds, CullingBounds (flag and bounds),
    ///         RootBone and RootTransform (flag, serialized reference ID, name), UseGpuMeshBvh,
    ///         RealtimeGpuMeshBvhForSkinnedMeshes, then the generic block (i32 length and bytes) of the mesh optimizer
    ///         settings and the LOD materials
    /// u16     LodCount
    /// u16     InlineLodIndex (the coarsest LOD: the last one in sort order)
    /// LOD descriptor x LodCount: 120 bytes of fixed fields, the stable sort key, and the material index (i32, -1 for none)
    /// bytes   the payload of the inline LOD (PayloadLength bytes of its descriptor)
    /// </code>
    /// <para>
    /// The fixed fields of a descriptor are: MaxVisibleDistance, MinProjectedScreenRadiusPixels, Flags,
    /// GeneratedTargetIndexRatio, GeneratedNormalizedError, GeneratedObjectSpaceError, Bounds, VertexCount, IndexCount,
    /// BoneCount, BlendshapeCount, BoneSetHash, BlendshapeNameHash, ContentKey (16 bytes, big endian), PayloadLength,
    /// PayloadChecksum, MeshCodecVersion, and a reserved zero.
    /// </para>
    /// <para>
    /// The writer writes only version 2. The reader accepts only version 2 in a <see cref="CookedAssetFormat.RuntimeBinaryV1"/>
    /// envelope; an older generic envelope (<see cref="CookedAssetFormat.BinaryV1"/> or <see cref="CookedAssetFormat.BinaryV2"/>)
    /// is version 1 by definition and loads through the generic reader with all LODs inline. A version above 2 is rejected.
    /// </para>
    /// <para>
    /// The writer puts each separate LOD entry in the <see cref="SubMeshLodPayloadCookSink"/> of the thread. The reader
    /// follows <see cref="SubMeshLodPayloads.LoadPolicy"/>: with <see cref="ELodPayloadLoadPolicy.All"/> it reads each
    /// separate LOD at once; with <see cref="ELodPayloadLoadPolicy.ProxyFirst"/> it reads none of them.
    /// </para>
    /// </remarks>
    public static class SubMeshCookedCodec
    {
        /// <summary>The version of the cooked sub-mesh payload that the writer writes.</summary>
        public const int CodecVersion = 2;

        /// <summary>The version of the mesh payload of a LOD: the cooked <see cref="XRMesh"/> runtime payload.</summary>
        public const uint MeshCodecVersion = 1;

        private const string OwnerName = "XREngine.Runtime.Rendering";

        /// <summary>Registers the codec with <see cref="PublishedCookedAssetRegistry"/>.</summary>
        internal static IDisposable Install()
            => PublishedCookedAssetRegistry.Register(
                typeof(SubMesh),
                static (asset, writer) => Write((SubMesh)asset, writer),
                static (payload, _) => Read(payload),
                OwnerName,
                dependencies: null);

        /// <summary>
        /// Writes the payload of a sub-mesh. Each LOD other than the coarsest goes to the open
        /// <see cref="SubMeshLodPayloadCookSink"/> as a separate entry.
        /// </summary>
        /// <param name="subMesh">The sub-mesh.</param>
        /// <param name="destination">The destination of the payload.</param>
        /// <exception cref="InvalidOperationException">The sub-mesh has more than one LOD and no sink is open.</exception>
        public static void Write(SubMesh subMesh, IBufferWriter<byte> destination)
        {
            ArgumentNullException.ThrowIfNull(subMesh);
            ArgumentNullException.ThrowIfNull(destination);

            SubMeshLOD[] lods = [.. subMesh.LODs];
            if (lods.Length > ushort.MaxValue)
                throw new InvalidOperationException($"Cooked sub-mesh '{subMesh.Name}' has {lods.Length} LODs; at most {ushort.MaxValue} can be cooked.");

            int inlineIndex = Math.Max(0, lods.Length - 1);
            SubMeshLodPayloadCookSink? sink = SubMeshLodPayloadCookSink.Current;
            if (lods.Length > 1 && sink is null)
                throw new InvalidOperationException(
                    $"Cooked sub-mesh '{subMesh.Name}' has {lods.Length} LODs, but no {nameof(SubMeshLodPayloadCookSink)} is open for the separate LOD entries.");

            SubMeshCookedReferenceSet references = new() { MeshOptimizer = subMesh.MeshOptimizer };
            Dictionary<XRMaterial, int> materialIndices = new(ReferenceEqualityComparer.Instance);
            int[] lodMaterials = new int[lods.Length];
            byte[]?[] payloads = new byte[]?[lods.Length];
            SubMeshLodDescriptor[] descriptors = new SubMeshLodDescriptor[lods.Length];
            for (int i = 0; i < lods.Length; i++)
            {
                SubMeshLOD lod = lods[i];
                lodMaterials[i] = GetMaterialIndex(lod.Material, references, materialIndices);
                bool inline = i == inlineIndex;
                byte[]? payload = lod.Mesh is null ? null : inline ? SerializeMesh(lod.Mesh) : SerializeMeshEntry(lod.Mesh);
                payloads[i] = payload;
                descriptors[i] = CreateDescriptor(lod, payload, inline);
                if (payload is not null && !inline)
                    sink!.Write(descriptors[i].EntryName, payload);
            }

            byte[] referenceBytes;
#pragma warning disable IL2026, IL3050 // The materials keep the generic cooked encoding of the older sub-mesh payload.
            referenceBytes = GenericCookedBinarySerializer.Serialize(references);
#pragma warning restore IL2026, IL3050

            using MemoryStream stream = new();
            using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((uint)CodecVersion);
                WriteGuid(writer, subMesh.ID);
                WriteNullableString(writer, subMesh.Name);
                WriteNullableString(writer, subMesh.OriginalPath);

                writer.Write(subMesh.ImportedEntityIdentity ?? string.Empty);
                writer.Write(subMesh.ImportedEntityIdentityIsStable);
                WriteBounds(writer, subMesh.Bounds);
                writer.Write(subMesh.CullingBounds.HasValue);
                WriteBounds(writer, subMesh.CullingBounds ?? default);
                WriteTransformReference(writer, subMesh.RootBone);
                WriteTransformReference(writer, subMesh.RootTransform);
                writer.Write(subMesh.UseGpuMeshBvh);
                writer.Write(subMesh.RealtimeGpuMeshBvhForSkinnedMeshes);
                writer.Write(referenceBytes.Length);
                writer.Write(referenceBytes);

                writer.Write((ushort)lods.Length);
                writer.Write((ushort)inlineIndex);
                for (int i = 0; i < lods.Length; i++)
                    WriteDescriptor(writer, lods[i], descriptors[i], lodMaterials[i]);

                if (lods.Length > 0 && payloads[inlineIndex] is { } inlinePayload)
                    writer.Write(inlinePayload);
            }

            destination.Write(stream.GetBuffer().AsSpan(0, (int)stream.Length));
        }

        /// <summary>
        /// Reads a payload of codec version 2. The separate LODs follow <see cref="SubMeshLodPayloads.LoadPolicy"/>.
        /// A separate LOD that does not load stays not resident, and one warning names the sub-mesh, the LOD, and the reason.
        /// </summary>
        /// <param name="payload">The payload.</param>
        /// <exception cref="InvalidDataException">The payload is not a valid version 2 payload.</exception>
        public static SubMesh Read(ReadOnlySpan<byte> payload)
        {
            SubMeshCookedPayloadReader reader = new(payload);
            uint version = reader.ReadUInt32();
            if (version != CodecVersion)
                throw new InvalidDataException(
                    $"The cooked sub-mesh payload has codec version {version}; this reader accepts version {CodecVersion} only. Cook the content again.");

            SubMesh subMesh = new();
            Guid id = reader.ReadGuid();
            if (id != Guid.Empty)
                subMesh.AdoptPersistentID(id);
            subMesh.Name = reader.ReadNullableString();
            subMesh.OriginalPath = reader.ReadNullableString();

            subMesh.ImportedEntityIdentity = reader.ReadString();
            subMesh.ImportedEntityIdentityIsStable = reader.ReadBoolean();
            subMesh.Bounds = ReadBounds(ref reader);
            bool hasCullingBounds = reader.ReadBoolean();
            AABB cullingBounds = ReadBounds(ref reader);
            subMesh.CullingBounds = hasCullingBounds ? cullingBounds : null;
            subMesh.RootBone = ReadTransformReference(ref reader);
            subMesh.RootTransform = ReadTransformReference(ref reader);
            subMesh.UseGpuMeshBvh = reader.ReadBoolean();
            subMesh.RealtimeGpuMeshBvhForSkinnedMeshes = reader.ReadBoolean();

            int referenceLength = reader.ReadInt32();
            SubMeshCookedReferenceSet references;
#pragma warning disable IL2026, IL3050 // The materials keep the generic cooked encoding of the older sub-mesh payload.
            references = GenericCookedBinarySerializer.Deserialize(typeof(SubMeshCookedReferenceSet), reader.ReadBytes(referenceLength)) as SubMeshCookedReferenceSet
                ?? throw new InvalidDataException("The cooked sub-mesh payload has no reference block.");
#pragma warning restore IL2026, IL3050
            if (references.MeshOptimizer is { } optimizer)
                subMesh.MeshOptimizer = optimizer;

            int lodCount = reader.ReadUInt16();
            int inlineIndex = reader.ReadUInt16();
            if (lodCount > 0 && inlineIndex != lodCount - 1)
                throw new InvalidDataException($"The cooked sub-mesh payload gives inline LOD {inlineIndex} of {lodCount}; the inline LOD must be the last one.");

            SubMeshLOD[] lods = new SubMeshLOD[lodCount];
            for (int i = 0; i < lodCount; i++)
                lods[i] = ReadDescriptor(ref reader, references.Materials);

            if (lodCount > 0)
            {
                SubMeshLOD inlineLod = lods[inlineIndex];
                SubMeshLodDescriptor descriptor = inlineLod.CookedDescriptor!;
                if (descriptor.PayloadLength > int.MaxValue)
                    throw new InvalidDataException($"The inline LOD payload of cooked sub-mesh '{subMesh.Name}' is too large.");
                if (descriptor.PayloadLength > 0)
                    inlineLod.Mesh = DeserializeMesh(reader.ReadBytes((int)descriptor.PayloadLength))
                        ?? throw new InvalidDataException($"The inline LOD payload of cooked sub-mesh '{subMesh.Name}' is not a cooked mesh.");
            }

            if (SubMeshLodPayloads.LoadPolicy == ELodPayloadLoadPolicy.All)
                LoadSeparateLods(subMesh.Name, lods);

            subMesh.LODs = new SortedSet<SubMeshLOD>(lods, new LODSorter());
            subMesh.CookedCodecVersion = CodecVersion;
            return subMesh;
        }

        /// <summary>
        /// Gets the size in bytes of the payload of the coarsest LOD of a sub-mesh: the payload length of its descriptor, or
        /// for a sub-mesh with no descriptor (an editor or an older cooked sub-mesh) the length that a cook would write.
        /// </summary>
        /// <param name="subMesh">The sub-mesh.</param>
        /// <returns>The size, or zero if the coarsest LOD has no mesh.</returns>
        public static long GetCoarsestLodPayloadLength(SubMesh subMesh)
        {
            ArgumentNullException.ThrowIfNull(subMesh);
            SubMeshLOD? coarsest = null;
            foreach (SubMeshLOD lod in subMesh.LODs)
                coarsest = lod;

            if (coarsest is null)
                return 0L;
            if (coarsest.CookedDescriptor is { } descriptor)
                return (long)Math.Min(descriptor.PayloadLength, long.MaxValue);
            if (coarsest.Mesh is not { } mesh)
                return 0L;

#pragma warning disable IL2026, IL3050 // The mesh uses its explicit runtime cooked serializer.
            return RuntimeCookedBinarySerializer.CalculateSize(mesh);
#pragma warning restore IL2026, IL3050
        }

        private static void LoadSeparateLods(string? subMeshName, SubMeshLOD[] lods)
        {
            for (int i = 0; i < lods.Length; i++)
            {
                SubMeshLOD lod = lods[i];
                if (!lod.IsPayloadDeferred || lod.Mesh is not null)
                    continue;

                if (SubMeshLodPayloads.TryLoadLod(lod.CookedDescriptor!, out XRMesh? mesh, out string? failure))
                    lod.Mesh = mesh;
                else
                    Debug.LogWarning(ELogCategory.Meshes, "Cooked sub-mesh '{0}' LOD {1} stays not resident: {2}.", subMeshName ?? string.Empty, i, failure ?? "unknown failure");
            }
        }

        private static int GetMaterialIndex(XRMaterial? material, SubMeshCookedReferenceSet references, Dictionary<XRMaterial, int> indices)
        {
            if (material is null)
                return -1;
            if (indices.TryGetValue(material, out int index))
                return index;

            index = references.Materials.Count;
            references.Materials.Add(material);
            indices.Add(material, index);
            return index;
        }

        private static byte[] SerializeMesh(XRMesh mesh)
        {
            if (!PublishedCookedAssetRegistry.TrySerialize(mesh, out byte[] payload))
                throw new InvalidOperationException($"No cooked codec is registered for '{typeof(XRMesh).FullName}'.");
            return payload;
        }

        private static byte[] SerializeMeshEntry(XRMesh mesh)
            => CookedAssetEnvelope.Serialize(new CookedAssetBlob(
                CookedAssetTypeReference.Encode(typeof(XRMesh)),
                CookedAssetFormat.RuntimeBinaryV1,
                SerializeMesh(mesh)));

        private static XRMesh? DeserializeMesh(ReadOnlySpan<byte> payload)
            => PublishedCookedAssetRegistry.TryDeserialize(typeof(XRMesh), payload, out object? asset) ? asset as XRMesh : null;

        private static SubMeshLodDescriptor CreateDescriptor(SubMeshLOD lod, byte[]? payload, bool inline)
        {
            XRMesh? mesh = lod.Mesh;
            ESubMeshLodFlags flags = ESubMeshLodFlags.None;
            if (lod.IsAutoGenerated)
                flags |= ESubMeshLodFlags.IsAutoGenerated;
            // A LOD with no mesh has no separate entry, so it counts as inline with an empty payload.
            if (inline || payload is null)
                flags |= ESubMeshLodFlags.IsInline;
            if (mesh?.HasSkinning == true)
                flags |= ESubMeshLodFlags.HasSkinning;
            if (mesh?.HasBlendshapes == true)
                flags |= ESubMeshLodFlags.HasBlendshapes;

            bool separate = payload is not null && !inline;
            return new SubMeshLodDescriptor
            {
                Flags = flags,
                Bounds = mesh?.Bounds ?? default,
                VertexCount = (uint)Math.Max(0, mesh?.VertexCount ?? 0),
                IndexCount = (uint)Math.Max(0, mesh?.IndexCount ?? 0),
                BoneCount = (uint)(mesh?.UtilizedBones?.Length ?? 0),
                BlendshapeCount = mesh?.BlendshapeCount ?? 0u,
                BoneSetHash = mesh is null ? 0UL : SubMeshLodHashes.ComputeBoneSetHash(mesh),
                BlendshapeNameHash = mesh is null ? 0UL : SubMeshLodHashes.ComputeBlendshapeNameHash(mesh),
                ContentKey = separate ? SubMeshLodHashes.ComputeContentKey(MeshCodecVersion, payload) : UInt128.Zero,
                PayloadLength = (ulong)(payload?.Length ?? 0),
                PayloadChecksum = payload is null ? 0UL : SubMeshLodHashes.ComputePayloadChecksum(payload),
                MeshCodecVersion = MeshCodecVersion,
            };
        }

        private static void WriteDescriptor(BinaryWriter writer, SubMeshLOD lod, SubMeshLodDescriptor descriptor, int materialIndex)
        {
            writer.Write(lod.MaxVisibleDistance);
            writer.Write(lod.MinProjectedScreenRadiusPixels);
            writer.Write((uint)descriptor.Flags);
            writer.Write(lod.GeneratedTargetIndexRatio);
            writer.Write(lod.GeneratedNormalizedError);
            writer.Write(lod.GeneratedObjectSpaceError);
            WriteBounds(writer, descriptor.Bounds);
            writer.Write(descriptor.VertexCount);
            writer.Write(descriptor.IndexCount);
            writer.Write(descriptor.BoneCount);
            writer.Write(descriptor.BlendshapeCount);
            writer.Write(descriptor.BoneSetHash);
            writer.Write(descriptor.BlendshapeNameHash);
            Span<byte> key = stackalloc byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt128BigEndian(key, descriptor.ContentKey);
            writer.Write(key);
            writer.Write(descriptor.PayloadLength);
            writer.Write(descriptor.PayloadChecksum);
            writer.Write(descriptor.MeshCodecVersion);
            writer.Write(0u);
            writer.Write(lod.StableSortKey ?? string.Empty);
            writer.Write(materialIndex);
        }

        private static SubMeshLOD ReadDescriptor(ref SubMeshCookedPayloadReader reader, List<XRMaterial> materials)
        {
            float maxVisibleDistance = reader.ReadSingle();
            float minProjectedRadius = reader.ReadSingle();
            ESubMeshLodFlags flags = (ESubMeshLodFlags)reader.ReadUInt32();
            float targetIndexRatio = reader.ReadSingle();
            float normalizedError = reader.ReadSingle();
            float objectSpaceError = reader.ReadSingle();
            AABB bounds = ReadBounds(ref reader);
            SubMeshLodDescriptor descriptor = new()
            {
                Flags = flags,
                Bounds = bounds,
                VertexCount = reader.ReadUInt32(),
                IndexCount = reader.ReadUInt32(),
                BoneCount = reader.ReadUInt32(),
                BlendshapeCount = reader.ReadUInt32(),
                BoneSetHash = reader.ReadUInt64(),
                BlendshapeNameHash = reader.ReadUInt64(),
                ContentKey = reader.ReadUInt128(),
                PayloadLength = reader.ReadUInt64(),
                PayloadChecksum = reader.ReadUInt64(),
                MeshCodecVersion = reader.ReadUInt32(),
            };
            _ = reader.ReadUInt32();
            string stableSortKey = reader.ReadString();
            int materialIndex = reader.ReadInt32();
            if (materialIndex < -1 || materialIndex >= materials.Count)
                throw new InvalidDataException($"A LOD descriptor of the cooked sub-mesh payload refers to material {materialIndex} of {materials.Count}.");

            return new SubMeshLOD(materialIndex < 0 ? null : materials[materialIndex], null, maxVisibleDistance, minProjectedRadius)
            {
                IsAutoGenerated = (flags & ESubMeshLodFlags.IsAutoGenerated) != 0,
                GeneratedTargetIndexRatio = targetIndexRatio,
                GeneratedNormalizedError = normalizedError,
                GeneratedObjectSpaceError = objectSpaceError,
                StableSortKey = stableSortKey,
                CookedDescriptor = descriptor,
            };
        }

        private static void WriteTransformReference(BinaryWriter writer, TransformBase? transform)
        {
            writer.Write(transform is not null);
            if (transform is null)
                return;
            WriteGuid(writer, transform.EffectiveSerializedReferenceId);
            WriteNullableString(writer, transform.Name);
        }

        /// <summary>
        /// Reads a transform reference as a carrier transform with the serialized reference ID, the same form that a cooked
        /// mesh uses for its bones. A <see cref="Components.Scene.Mesh.RenderableMesh"/> resolves it against its instance.
        /// </summary>
        private static TransformBase? ReadTransformReference(ref SubMeshCookedPayloadReader reader)
        {
            if (!reader.ReadBoolean())
                return null;

            Guid id = reader.ReadGuid();
            string? name = reader.ReadNullableString();
            return new Transform { Name = name, SerializedReferenceId = id };
        }

        private static void WriteBounds(BinaryWriter writer, AABB bounds)
        {
            WriteVector3(writer, bounds.Min);
            WriteVector3(writer, bounds.Max);
        }

        private static AABB ReadBounds(ref SubMeshCookedPayloadReader reader)
            => new(reader.ReadVector3(), reader.ReadVector3());

        private static void WriteVector3(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.X);
            writer.Write(value.Y);
            writer.Write(value.Z);
        }

        private static void WriteGuid(BinaryWriter writer, Guid value)
        {
            Span<byte> bytes = stackalloc byte[16];
            value.TryWriteBytes(bytes);
            writer.Write(bytes);
        }

        private static void WriteNullableString(BinaryWriter writer, string? value)
        {
            writer.Write(value is not null);
            if (value is not null)
                writer.Write(value);
        }
    }
}
