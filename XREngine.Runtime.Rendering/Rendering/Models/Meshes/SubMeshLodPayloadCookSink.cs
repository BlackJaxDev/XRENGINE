namespace XREngine.Rendering.Models
{
    /// <summary>
    /// Receives the separate LOD payload entries that the cooked <see cref="SubMesh"/> writer makes. A content cook opens a
    /// sink with <see cref="Begin"/> around the cook of its files. The writer of a sub-mesh with more than one LOD fails
    /// when no sink is open on the thread, because it has no place for the other LODs.
    /// </summary>
    /// <remarks>
    /// Each entry is the file <c>&lt;root&gt;/lod/&lt;key&gt;</c>. Equal payloads have the same content key, so a second write
    /// of a key that exists does nothing. This removes the duplicate bytes of a mesh that two sub-meshes share. The sink also
    /// keeps the description of each mesh that it met, so the size pass and the write of a generic stream, and a mesh that
    /// two sub-meshes share, serialize the mesh one time. Do not edit a mesh while a sink is open.
    /// </remarks>
    public sealed class SubMeshLodPayloadCookSink : IDisposable
    {
        [ThreadStatic]
        private static SubMeshLodPayloadCookSink? _current;

        private readonly SubMeshLodPayloadCookSink? _previous;
        // Weak keys: a sink lives for a whole cook and must not keep the meshes of each cooked file alive.
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<XRMesh, SubMeshLodDescriptor> _separate = new();
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<XRMesh, SubMeshLodDescriptor> _inline = new();
        private bool _disposed;

        private SubMeshLodPayloadCookSink(string rootDirectory, SubMeshLodPayloadCookSink? previous)
        {
            RootDirectory = rootDirectory;
            _previous = previous;
        }

        /// <summary>The sink that is open on the current thread, or null.</summary>
        public static SubMeshLodPayloadCookSink? Current => _current;

        /// <summary>The folder of the cooked content. The entries go to its <c>lod</c> folder.</summary>
        public string RootDirectory { get; }

        /// <summary>The number of entries that this sink wrote. A key that existed already does not count.</summary>
        public int WrittenEntryCount { get; private set; }

        /// <summary>Opens a sink on the current thread until the result is disposed. Sinks can nest.</summary>
        /// <param name="rootDirectory">The folder of the cooked content.</param>
        public static SubMeshLodPayloadCookSink Begin(string rootDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
            SubMeshLodPayloadCookSink sink = new(Path.GetFullPath(rootDirectory), _current);
            _current = sink;
            return sink;
        }

        /// <summary>Writes one entry if no entry with the same name exists.</summary>
        /// <param name="entryName">The entry name, <c>lod/&lt;key&gt;</c>.</param>
        /// <param name="payload">The entry bytes.</param>
        public void Write(string entryName, ReadOnlySpan<byte> payload)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
            ObjectDisposedException.ThrowIf(_disposed, this);

            string path = Path.Combine(RootDirectory, entryName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(payload);
            WrittenEntryCount++;
        }

        /// <summary>Gets the kept description of a mesh that this sink met before, or null.</summary>
        internal SubMeshLodDescriptor? GetDescribed(XRMesh mesh, bool separate)
            => (separate ? _separate : _inline).TryGetValue(mesh, out SubMeshLodDescriptor? descriptor) ? descriptor : null;

        /// <summary>Keeps the description of a mesh, so that the cook does not serialize the mesh again.</summary>
        internal void SetDescribed(XRMesh mesh, bool separate, SubMeshLodDescriptor descriptor)
            => (separate ? _separate : _inline).AddOrUpdate(mesh, descriptor);

        /// <summary>Closes the sink and opens the previous sink of the thread again.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (ReferenceEquals(_current, this))
                _current = _previous;
        }
    }
}
