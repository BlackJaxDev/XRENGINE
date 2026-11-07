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
    /// envelope, and rejects a version above 2. A sub-mesh in a generic stream (<see cref="CookedAssetFormat.BinaryV1"/> or
    /// <see cref="CookedAssetFormat.BinaryV2"/>, for example inside a cooked model) uses the nested-object hook of the generic
    /// serializer and its own version marker (see <see cref="SubMesh"/>); one with no marker is version 1, with all LODs inline.
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
                // The payload embeds the materials and meshes; the lod/<key> entries are not separate assets.
                static _ => Array.Empty<PublishedCookedAssetDependency>(),
                static asset => Accepts((SubMesh)asset));

        /// <summary>
        /// True if the registered codec writes a sub-mesh now: a <see cref="SubMeshLodPayloadCookSink"/> is open, or the
        /// sub-mesh has at most one LOD. Otherwise the codec declines, and the cook writes the sub-mesh with the generic cooked
        /// serializer, with every LOD inline. That is the same no-sink fallback that a sub-mesh inside a model uses, for
        /// example in a browser cook, which opens no sink.
        /// </summary>
        /// <param name="subMesh">The sub-mesh.</param>
        public static bool Accepts(SubMesh subMesh)
        {
            ArgumentNullException.ThrowIfNull(subMesh);
            return SubMeshLodPayloadCookSink.Current is not null || subMesh.LODs.Count <= 1;
        }

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
            byte[]? inlinePayload = null;
            SubMeshLodDescriptor[] descriptors = new SubMeshLodDescriptor[lods.Length];
            for (int i = 0; i < lods.Length; i++)
            {
                lodMaterials[i] = GetMaterialIndex(lods[i].Material, references, materialIndices);
                bool inline = i == inlineIndex;
                descriptors[i] = DescribeLod(subMesh, lods[i], inline, sink, inline ? (bytes => inlinePayload = bytes) : null);
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

                if (inlinePayload is not null)
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
                {
                    if (descriptor.MeshCodecVersion != MeshCodecVersion)
                        throw new InvalidDataException($"The inline LOD payload of cooked sub-mesh '{subMesh.Name}' has mesh codec version {descriptor.MeshCodecVersion}, not {MeshCodecVersion}.");
                    ReadOnlySpan<byte> inlineBytes = reader.ReadBytes((int)descriptor.PayloadLength);
                    if (SubMeshLodHashes.ComputePayloadChecksum(inlineBytes) != descriptor.PayloadChecksum)
                        throw new InvalidDataException($"The inline LOD payload of cooked sub-mesh '{subMesh.Name}' does not agree with its checksum.");
                    inlineLod.Mesh = DeserializeMesh(inlineBytes)
                        ?? throw new InvalidDataException($"The inline LOD payload of cooked sub-mesh '{subMesh.Name}' is not a cooked mesh.");
                }
            }
            if (reader.Remaining != 0)
                throw new InvalidDataException($"The cooked sub-mesh payload of '{subMesh.Name}' has {reader.Remaining} bytes after its last part.");

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

        /// <summary>
        /// Makes the LOD list that a generic cooked stream writes for a sub-mesh while a <see cref="SubMeshLodPayloadCookSink"/>
        /// is open: a copy of each LOD with its descriptor. Each LOD other than the coarsest goes to the sink as a separate
        /// entry, and its copy has no mesh. The copies keep the material references, so the stream shares a material that
        /// more than one LOD or sub-mesh uses through its reference table.
        /// </summary>
        internal static List<SubMeshLOD> CreateSplitView(SubMesh subMesh, SubMeshLodPayloadCookSink sink)
        {
            SubMeshLOD[] lods = [.. subMesh.LODs];
            List<SubMeshLOD> view = new(lods.Length);
            for (int i = 0; i < lods.Length; i++)
            {
                SubMeshLOD lod = lods[i];
                bool inline = i == lods.Length - 1;
                SubMeshLodDescriptor descriptor = DescribeLod(subMesh, lod, inline, sink, inlinePayload: null);
                view.Add(new SubMeshLOD(lod.Material, inline || descriptor.IsInline ? lod.Mesh : null, lod.MaxVisibleDistance, lod.MinProjectedScreenRadiusPixels)
                {
                    GenerateAsync = lod.GenerateAsync,
                    IsAutoGenerated = lod.IsAutoGenerated,
                    GeneratedTargetIndexRatio = lod.GeneratedTargetIndexRatio,
                    GeneratedNormalizedError = lod.GeneratedNormalizedError,
                    GeneratedObjectSpaceError = lod.GeneratedObjectSpaceError,
                    StableSortKey = lod.StableSortKey,
                    CookedDescriptor = descriptor,
                });
            }
            return view;
        }

        /// <summary>
        /// Loads each separate LOD of a sub-mesh that is not resident, as a load with <see cref="ELodPayloadLoadPolicy.All"/>
        /// does. A LOD that does not load stays not resident with one warning.
        /// </summary>
        internal static void LoadSeparateLods(string? subMeshName, SubMeshLOD[] lods)
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

        /// <summary>
        /// Describes one LOD. A separate LOD writes its entry to the sink. The mesh part of a description is kept in the sink
        /// for each mesh, so a mesh that the cook meets again (the size pass and the write of a generic stream, or a mesh that
        /// two sub-meshes share) is not serialized again. A LOD that is a separate payload with no mesh (a sub-mesh that a
        /// load with <see cref="ELodPayloadLoadPolicy.ProxyFirst"/> gave) cannot be cooked, because its bytes are not in
        /// memory and the new output would refer to a key that it does not hold.
        /// </summary>
        /// <param name="subMesh">The sub-mesh, for the error text.</param>
        /// <param name="lod">The LOD.</param>
        /// <param name="inline">True for the coarsest LOD, whose payload is inline.</param>
        /// <param name="sink">The sink of the separate entries. A separate LOD needs it.</param>
        /// <param name="inlinePayload">Receives the inline payload bytes, or null if the caller does not need them.</param>
        /// <exception cref="InvalidDataException">The LOD is a separate payload that is not resident.</exception>
        internal static SubMeshLodDescriptor DescribeLod(SubMesh subMesh, SubMeshLOD lod, bool inline, SubMeshLodPayloadCookSink? sink, Action<byte[]>? inlinePayload)
        {
            ESubMeshLodFlags lodFlags = ESubMeshLodFlags.None;
            if (lod.IsAutoGenerated)
                lodFlags |= ESubMeshLodFlags.IsAutoGenerated;
            if (lod.GenerateAsync)
                lodFlags |= ESubMeshLodFlags.GenerateAsync;

            if (lod.Mesh is not { } mesh)
            {
                // InvalidDataException, not InvalidOperationException: the generic serializer reads the latter as
                // "MemoryPack does not support this type" and would retry the write with another encoding.
                if (lod.CookedDescriptor is { IsInline: false })
                    throw new InvalidDataException(
                        $"Sub-mesh '{subMesh.Name}' ({subMesh.ID}) has LOD '{lod.StableSortKey}' with a separate payload that is not resident, so it cannot be cooked. Cook from the source asset, or load all LODs first.");

                // A LOD with no mesh has no separate entry, so it counts as inline with an empty payload.
                return new SubMeshLodDescriptor { Flags = lodFlags | ESubMeshLodFlags.IsInline, MeshCodecVersion = MeshCodecVersion };
            }

            SubMeshLodDescriptor described;
            if (inline)
            {
                if (inlinePayload is not null)
                {
                    byte[] bytes = SerializeMesh(mesh);
                    inlinePayload(bytes);
                    described = sink?.GetDescribed(mesh, separate: false) ?? DescribeMesh(mesh, bytes, separate: false);
                }
                else
                    described = sink?.GetDescribed(mesh, separate: false) ?? DescribeMesh(mesh, SerializeMesh(mesh), separate: false);
                sink?.SetDescribed(mesh, separate: false, described);
            }
            else
            {
                if (sink is null)
                    throw new InvalidOperationException($"The LOD mesh '{mesh.Name}' needs a separate entry, but no {nameof(SubMeshLodPayloadCookSink)} is open.");
                if (sink.GetDescribed(mesh, separate: true) is not { } cached)
                {
                    byte[] entry = SerializeMeshEntry(mesh);
                    cached = DescribeMesh(mesh, entry, separate: true);
                    sink.Write(cached.EntryName, entry);
                    sink.SetDescribed(mesh, separate: true, cached);
                }
                described = cached;
            }
            return described with { Flags = described.Flags | lodFlags };
        }

        private static SubMeshLodDescriptor DescribeMesh(XRMesh mesh, byte[] payload, bool separate)
        {
            ESubMeshLodFlags flags = separate ? ESubMeshLodFlags.None : ESubMeshLodFlags.IsInline;
            if (mesh.HasSkinning)
                flags |= ESubMeshLodFlags.HasSkinning;
            if (mesh.HasBlendshapes)
                flags |= ESubMeshLodFlags.HasBlendshapes;

            return new SubMeshLodDescriptor
            {
                Flags = flags,
                Bounds = mesh.Bounds,
                VertexCount = (uint)Math.Max(0, mesh.VertexCount),
                IndexCount = (uint)Math.Max(0, mesh.IndexCount),
                BoneCount = (uint)(mesh.UtilizedBones?.Length ?? 0),
                BlendshapeCount = mesh.BlendshapeCount,
                BoneSetHash = SubMeshLodHashes.ComputeBoneSetHash(mesh),
                BlendshapeNameHash = SubMeshLodHashes.ComputeBlendshapeNameHash(mesh),
                ContentKey = separate ? SubMeshLodHashes.ComputeContentKey(MeshCodecVersion, payload) : UInt128.Zero,
                PayloadLength = (ulong)payload.Length,
                PayloadChecksum = SubMeshLodHashes.ComputePayloadChecksum(payload),
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
                GenerateAsync = (flags & ESubMeshLodFlags.GenerateAsync) != 0,
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
