using MemoryPack;
using XREngine.Data;
using YamlDotNet.Serialization;

namespace XREngine.Rendering
{
    public partial class XRDataBuffer
    {
        private EXRBufferClientCopyPolicy _clientCopyPolicy = EXRBufferClientCopyPolicy.Default;
        private int _clientSpillQueued;
        // Changes on every explicit client write: writer begin and end, commits,
        // pushes and the legacy setters. A spill whose copy overlapped a change is
        // abandoned, so the mapping never misses bytes written to the private copy.
        private ulong _clientWriteActivity;
        // Scoped writers between Alloc and Commit/Cancel hold a pointer into the
        // current source; no spill may start or complete while one is open.
        private int _activeClientWriters;
        // The private copy the spill timer is reading, set under _writeModelSync.
        // Releasing or replacing it while leased hands its disposal to the spill.
        private DataSource? _clientSpillReadSource;
        private bool _clientSpillReadSourceReleased;

        /// <summary>
        /// What happens to this buffer's CPU copy once its GPU copy is current.
        /// <see cref="EXRBufferClientCopyPolicy.Default"/> defers to the engine's mesh
        /// buffer policy for mesh-owned buffers and retains the copy otherwise.
        /// </summary>
        [YamlIgnore]
        [MemoryPackIgnore]
        public EXRBufferClientCopyPolicy ClientCopyPolicy
        {
            get => _clientCopyPolicy;
            set => SetField(ref _clientCopyPolicy, value);
        }

        /// <summary>The policy in force after resolving <see cref="EXRBufferClientCopyPolicy.Default"/>.</summary>
        [YamlIgnore]
        [MemoryPackIgnore]
        public EXRBufferClientCopyPolicy EffectiveClientCopyPolicy
            => _clientCopyPolicy != EXRBufferClientCopyPolicy.Default
                ? _clientCopyPolicy
                : IsMeshOwnedBuffer
                    ? RuntimeEngine.Rendering.Settings.MeshBufferClientCopyPolicy
                    : EXRBufferClientCopyPolicy.Retain;

        /// <summary>True once the CPU copy lives in a session spill mapping.</summary>
        [YamlIgnore]
        [MemoryPackIgnore]
        public bool IsClientCopySpilled => _clientSideSource is XRBufferSpilledDataSource;

        /// <summary>
        /// Called when a backend reports the GPU copy current for this revision.
        /// Queues the spill of a <see cref="EXRBufferClientCopyPolicy.ReleaseAfterUpload"/>
        /// copy; the spill itself runs later on the spill timer, never here.
        /// </summary>
        private void QueueClientSpillIfEligible()
        {
            if (!IsClientSpillCandidate() || Interlocked.Exchange(ref _clientSpillQueued, 1) != 0)
                return;

            XRBufferClientSpill.Enqueue(this);
        }

        private bool IsClientSpillCandidate()
            => EffectiveClientCopyPolicy == EXRBufferClientCopyPolicy.ReleaseAfterUpload &&
               Usage is (EBufferUsage.StaticDraw or EBufferUsage.StaticRead or EBufferUsage.StaticCopy) &&
               !GpuProduced &&
               !DisposeOnPush &&
               !HasGpuCompressedPayload &&
               _clientSideSource is { External: false } source &&
               source.Length >= XRBufferClientSpill.MinimumSpillBytes;

        /// <summary>
        /// Records an explicit client write. Called after the bytes are written, so a
        /// spill copy that overlapped the write observes the change at completion.
        /// </summary>
        private void NoteClientWrite()
            => Volatile.Write(ref _clientWriteActivity, _clientWriteActivity + 1UL);

        /// <summary>Marks a scoped writer as open (see <see cref="EndClientWriter"/>).</summary>
        internal void BeginClientWriter()
        {
            Interlocked.Increment(ref _activeClientWriters);
            NoteClientWrite();
        }

        /// <summary>Closes a scoped writer opened by an <c>Alloc</c> call, committed or not.</summary>
        internal void EndClientWriter()
        {
            NoteClientWrite();
            Interlocked.Decrement(ref _activeClientWriters);
        }

        /// <summary>
        /// Disposes a private copy this buffer is releasing or replacing, unless the
        /// spill timer is reading it; the spill then disposes it when the read ends.
        /// </summary>
        private void DisposeReleasedClientSource(DataSource? source)
        {
            if (source is null)
                return;

            lock (_writeModelSync)
            {
                if (ReferenceEquals(source, _clientSpillReadSource))
                {
                    _clientSpillReadSourceReleased = true;
                    return;
                }
            }

            source.Dispose();
        }

        /// <summary>
        /// Captures the private copy to spill if this buffer is still a candidate, its
        /// GPU copy is current for the present revision and no writer is open. The copy
        /// stays leased to the caller until <see cref="TryCompleteClientSpill"/>.
        /// </summary>
        internal bool TryBeginClientSpill(out DataSource source, out ulong revision, out ulong writeActivity)
        {
            lock (_writeModelSync)
            {
                Volatile.Write(ref _clientSpillQueued, 0);
                source = _clientSideSource!;
                revision = _revision;
                writeActivity = Volatile.Read(ref _clientWriteActivity);
                bool eligible = !IsDestroyed &&
                    IsClientSpillCandidate() &&
                    _uploadedRevision == _revision &&
                    !_pendingWriterUpload &&
                    Volatile.Read(ref _activeClientWriters) == 0 &&
                    ActivelyMapping.Count == 0;
                if (eligible)
                {
                    _clientSpillReadSource = source;
                    _clientSpillReadSourceReleased = false;
                }
                return eligible;
            }
        }

        /// <summary>
        /// Ends the spill read begun by <see cref="TryBeginClientSpill"/> and swaps in
        /// <paramref name="spilled"/> (null when writing the file failed) if nothing
        /// wrote to or replaced the copy meanwhile. The contents are identical, so no
        /// change notification is raised: raising one would re-upload unchanged data.
        /// </summary>
        /// <param name="disposeExpected">
        /// True when the buffer released or replaced <paramref name="expected"/> during
        /// the read; the caller must dispose it.
        /// </param>
        internal bool TryCompleteClientSpill(
            DataSource expected,
            XRBufferSpilledDataSource? spilled,
            ulong revision,
            ulong writeActivity,
            out bool disposeExpected)
        {
            lock (_writeModelSync)
            {
                disposeExpected = ReferenceEquals(_clientSpillReadSource, expected) && _clientSpillReadSourceReleased;
                _clientSpillReadSource = null;
                _clientSpillReadSourceReleased = false;
                if (spilled is null ||
                    !ReferenceEquals(_clientSideSource, expected) ||
                    _revision != revision ||
                    Volatile.Read(ref _clientWriteActivity) != writeActivity ||
                    Volatile.Read(ref _activeClientWriters) != 0 ||
                    _pendingWriterUpload ||
                    IsDestroyed)
                {
                    return false;
                }

                _clientSideSource = spilled;
                return true;
            }
        }
    }
}
