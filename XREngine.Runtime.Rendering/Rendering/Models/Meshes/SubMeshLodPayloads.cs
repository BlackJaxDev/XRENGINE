using XREngine.Core.Files;

namespace XREngine.Rendering.Models
{
    /// <summary>
    /// Reads the separate LOD payloads of cooked <see cref="SubMesh"/> assets and holds the load policy of a sub-mesh load.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The load policy is <see cref="ELodPayloadLoadPolicy.All"/> by default, so a world load gives every LOD as before the
    /// payload split. A host that wants the coarsest LOD first, for example a cutscene that spawns content, sets
    /// <see cref="ELodPayloadLoadPolicy.ProxyFirst"/> for its loads with <see cref="UseLoadPolicy"/>. The scope flows with
    /// the asynchronous context of the caller.
    /// </para>
    /// <para>
    /// A LOD read never cooks again. A missing entry, a bad length, a bad checksum, an unknown mesh codec version, or a hash
    /// that does not agree rejects that LOD only, with a reason. Call <see cref="TryLoadLod"/> on a job thread or a load
    /// thread, never on the render thread: it reads the disk and hashes the bytes.
    /// </para>
    /// </remarks>
    public static class SubMeshLodPayloads
    {
        private static readonly Lock SourcesSync = new();
        private static readonly AsyncLocal<ELodPayloadLoadPolicy?> PolicyOverride = new();
        private static ISubMeshLodPayloadSource[] _sources = [PublishedArchiveLodPayloadSource.Instance];
        private static int _defaultLoadPolicy = (int)ELodPayloadLoadPolicy.All;

        /// <summary>The policy of a load with no scope. The default is <see cref="ELodPayloadLoadPolicy.All"/>.</summary>
        public static ELodPayloadLoadPolicy DefaultLoadPolicy
        {
            get => (ELodPayloadLoadPolicy)Volatile.Read(ref _defaultLoadPolicy);
            set => Volatile.Write(ref _defaultLoadPolicy, (int)value);
        }

        /// <summary>The policy that a sub-mesh load uses now: the policy of the current scope, or <see cref="DefaultLoadPolicy"/>.</summary>
        public static ELodPayloadLoadPolicy LoadPolicy
            => PolicyOverride.Value ?? DefaultLoadPolicy;

        /// <summary>Sets the load policy until the result is disposed. Scopes can nest.</summary>
        /// <param name="policy">The policy.</param>
        public static IDisposable UseLoadPolicy(ELodPayloadLoadPolicy policy)
        {
            ELodPayloadLoadPolicy? previous = PolicyOverride.Value;
            PolicyOverride.Value = policy;
            return new PolicyScope(previous);
        }

        /// <summary>
        /// Adds a payload source in front of the others until the result is disposed. The published archive source is
        /// always present.
        /// </summary>
        /// <param name="source">The source.</param>
        public static IDisposable RegisterSource(ISubMeshLodPayloadSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            lock (SourcesSync)
                _sources = [source, .. _sources];
            return new SourceRegistration(source);
        }

        /// <summary>
        /// Reads, checks, and parses the payload of a LOD that is not inline. The method does not change the sub-mesh.
        /// </summary>
        /// <param name="descriptor">The descriptor of the LOD.</param>
        /// <param name="mesh">Receives the mesh, or null on a failure.</param>
        /// <param name="failure">Receives the reason of a failure, or null.</param>
        /// <returns>True if the mesh is ready to publish.</returns>
        public static bool TryLoadLod(SubMeshLodDescriptor descriptor, out XRMesh? mesh, out string? failure)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            mesh = null;
            if (descriptor.IsInline)
            {
                failure = "the LOD is inline and has no separate payload";
                return false;
            }
            if (descriptor.MeshCodecVersion != SubMeshCookedCodec.MeshCodecVersion)
            {
                failure = $"the mesh codec version {descriptor.MeshCodecVersion} is not supported (expected {SubMeshCookedCodec.MeshCodecVersion})";
                return false;
            }

            string entryName = descriptor.EntryName;
            ISubMeshLodPayloadSource[] sources = Volatile.Read(ref _sources);
            CookedPayloadOwner? payload = null;
            try
            {
                for (int i = 0; i < sources.Length && payload is null; i++)
                    if (!sources[i].TryOpen(entryName, out payload))
                        payload = null;

                if (payload is null)
                {
                    failure = $"the entry '{entryName}' was not found";
                    return false;
                }

                return TryParse(descriptor, payload.Span, out mesh, out failure);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or NotSupportedException or ObjectDisposedException)
            {
                mesh?.Destroy(now: true);
                mesh = null;
                failure = $"the entry '{entryName}' could not be read: {ex.Message}";
                return false;
            }
            finally
            {
                ((IDisposable?)payload)?.Dispose();
            }
        }

        /// <summary>Checks and parses the bytes of a LOD payload entry against its descriptor.</summary>
        internal static bool TryParse(SubMeshLodDescriptor descriptor, ReadOnlySpan<byte> entry, out XRMesh? mesh, out string? failure)
        {
            mesh = null;
            if ((ulong)entry.Length != descriptor.PayloadLength)
            {
                failure = $"the payload length is {entry.Length} bytes, but the descriptor gives {descriptor.PayloadLength}";
                return false;
            }
            if (SubMeshLodHashes.ComputePayloadChecksum(entry) != descriptor.PayloadChecksum)
            {
                failure = "the payload checksum does not agree with the descriptor";
                return false;
            }

            CookedAssetEnvelopeHeader header = CookedAssetEnvelope.ParseHeader(entry);
            if (header.Format != CookedAssetFormat.RuntimeBinaryV1)
            {
                failure = $"the payload uses the cooked format '{header.Format}', not '{CookedAssetFormat.RuntimeBinaryV1}'";
                return false;
            }
            if (!PublishedCookedAssetRegistry.TryDeserialize(typeof(XRMesh), header.Payload(entry), out object? asset) || asset is not XRMesh parsed)
            {
                failure = "the payload is not a cooked mesh";
                return false;
            }

            if (SubMeshLodHashes.ComputeBoneSetHash(parsed) != descriptor.BoneSetHash)
            {
                parsed.Destroy(now: true);
                failure = "the bone set of the mesh does not agree with the descriptor";
                return false;
            }
            if (SubMeshLodHashes.ComputeBlendshapeNameHash(parsed) != descriptor.BlendshapeNameHash)
            {
                parsed.Destroy(now: true);
                failure = "the blendshape names of the mesh do not agree with the descriptor";
                return false;
            }

            mesh = parsed;
            failure = null;
            return true;
        }

        private sealed class PolicyScope(ELodPayloadLoadPolicy? previous) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                PolicyOverride.Value = previous;
            }
        }

        private sealed class SourceRegistration(ISubMeshLodPayloadSource source) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                lock (SourcesSync)
                {
                    ISubMeshLodPayloadSource[] current = _sources;
                    int index = Array.IndexOf(current, source);
                    if (index < 0)
                        return;
                    ISubMeshLodPayloadSource[] next = new ISubMeshLodPayloadSource[current.Length - 1];
                    Array.Copy(current, 0, next, 0, index);
                    Array.Copy(current, index + 1, next, index, current.Length - index - 1);
                    _sources = next;
                }
            }
        }
    }
}
