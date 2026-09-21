using XREngine.Extensions;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Objects;
using System.Diagnostics;

namespace XREngine.Rendering
{
    public partial class XRMesh
    {
        private bool _maxBlendshapeAccumulation = false;
        public bool MaxBlendshapeAccumulation
        {
            get => _maxBlendshapeAccumulation;
            set => SetField(ref _maxBlendshapeAccumulation, value);
        }

        private bool _supportsBillboarding = false;
        /// <summary>
        /// If true, the vertex shader will include billboarding code.
        /// Use the 'BillboardMode' engine uniform to set the type of billboarding.
        /// </summary>
        public bool SupportsBillboarding
        {
            get => _supportsBillboarding;
            set => SetField(ref _supportsBillboarding, value);
        }
        public bool HasNormals => NormalsBuffer != null || (Interleaved && NormalOffset.HasValue);
        public bool HasTangents => TangentsBuffer != null || (Interleaved && TangentOffset.HasValue);
        public bool HasColors => ColorCount > 0;
        public bool HasTexCoords => TexCoordCount > 0;

        public int IndexCount => _type switch
        {
            EPrimitiveType.Triangles => _triangles?.Count * 3 ?? 0,
            EPrimitiveType.Lines => _lines?.Count * 2 ?? 0,
            EPrimitiveType.Points => _points?.Count ?? 0,
            EPrimitiveType.LineStrip => _lines?.Count + 1 ?? 0,
            EPrimitiveType.LineLoop => _lines?.Count ?? 0,
            EPrimitiveType.TriangleStrip => _triangles?.Count + 2 ?? 0,
            EPrimitiveType.TriangleFan => _triangles?.Count + 2 ?? 0,
            EPrimitiveType.Quads => throw new NotImplementedException(),
            EPrimitiveType.QuadStrip => throw new NotImplementedException(),
            EPrimitiveType.Polygon => throw new NotImplementedException(),
            EPrimitiveType.LinesAdjacency => throw new NotImplementedException(),
            EPrimitiveType.LineStripAdjacency => throw new NotImplementedException(),
            EPrimitiveType.TrianglesAdjacency => throw new NotImplementedException(),
            EPrimitiveType.TriangleStripAdjacency => throw new NotImplementedException(),
            EPrimitiveType.Patches => PatchVertices switch
            {
                1 => _points?.Count ?? 0,
                2 => _lines?.Count * 2 ?? 0,
                3 => _triangles?.Count * 3 ?? 0,
                _ => 0,
            },
            _ => throw new NotImplementedException(),
        };

        public class BufferCollection : XRBase, IEventDictionary<string, XRDataBuffer>
        {
            private EventDictionary<string, XRDataBuffer> _buffers = [];
            private bool _ownsBufferData = true;
            private readonly object _mutationLock = new();
            private long _mutationRevision;
            private XRMesh? _owner;
            private bool _retired;
            private bool _ownerDestructionPending;
            private int _publicationLeaseDepth;
            private int _publicationLeaseThreadId;

            public BufferCollection()
            {
            }

            private BufferCollection(bool ownsBufferData)
            {
                _ownsBufferData = ownsBufferData;
            }

            internal long MutationRevision => Volatile.Read(ref _mutationRevision);

            internal bool IsPublicationLeaseHeldByCurrentThread
                => _publicationLeaseDepth > 0 &&
                   _publicationLeaseThreadId == Environment.CurrentManagedThreadId;

            internal void AttachOwner(XRMesh owner)
            {
                lock (_mutationLock)
                {
                    ThrowIfRetired();
                    _owner = owner;
                    _ownerDestructionPending = false;
                }
            }

            internal void DetachOwner(XRMesh owner)
            {
                lock (_mutationLock)
                    if (ReferenceEquals(_owner, owner))
                        _owner = null;
            }

            internal void BeginOwnerDestruction(XRMesh owner)
            {
                lock (_mutationLock)
                {
                    if (_retired || !ReferenceEquals(_owner, owner))
                        return;
                    _ownerDestructionPending = true;
                }
            }

            internal void CancelOwnerDestruction(XRMesh owner)
            {
                lock (_mutationLock)
                    if (!_retired && ReferenceEquals(_owner, owner))
                        _ownerDestructionPending = false;
            }

            internal PreparedBufferTicket CapturePreparationTicket(
                IEnumerable<string> keys,
                bool includeGeometryRevision = false)
            {
                ArgumentNullException.ThrowIfNull(keys);
                lock (_mutationLock)
                {
                    ThrowIfRetired();
                    KeyValuePair<string, XRDataBuffer?>[] entries =
                    [
                        .. keys.Distinct(StringComparer.Ordinal).Select(key =>
                            new KeyValuePair<string, XRDataBuffer?>(
                                key,
                                _buffers.TryGetValue(key, out XRDataBuffer? value) ? value : null)),
                    ];
                    return new PreparedBufferTicket(
                        this,
                        _owner,
                        entries,
                        includeGeometryRevision ? _owner?.GeometryRevision : null);
                }
            }

            /// <summary>
            /// Installs all prepared replacements and removals as one observable state
            /// change. Use it as the before-publication action of the reversible
            /// <see cref="RenderObjectPublicationScope.Complete(Action, Action, Action)"/>
            /// overload after all replacement buffers are prepared. Use
            /// <see cref="RestorePreparedBatch"/> for rollback and
            /// <see cref="DisposeReplacedBuffers"/> only after durable publication.
            /// </summary>
            internal PreparedBufferBatch SwapPreparedBatch(
                IEnumerable<KeyValuePair<string, XRDataBuffer>> replacements,
                IEnumerable<string>? removals = null,
                long? expectedMutationRevision = null,
                long? expectedGeometryRevision = null,
                PreparedBufferTicket? expectedTicket = null,
                Action? installState = null,
                Action? restoreStateOnFailure = null)
            {
                ArgumentNullException.ThrowIfNull(replacements);

                KeyValuePair<string, XRDataBuffer>[] preparedReplacements = [.. replacements];
                string[] preparedRemovals = removals is null ? [] : [.. removals];
                KeyValuePair<string, XRDataBuffer>[]? before = null;
                bool stateInstalled = false;
                bool leaseRegistered = false;
                // The monitor intentionally remains held after this method returns.
                // RestorePreparedBatch/DisposeReplacedBuffers releases it at the root
                // publication boundary, linearizing owner teardown and concurrent edits
                // with the complete apply/publish/rollback lifetime.
                Monitor.Enter(_mutationLock);
                try
                {
                    ThrowIfRetired();
                    ValidateExpectedMutationRevision(expectedMutationRevision, expectedGeometryRevision);
                    ValidatePreparationTicket(expectedTicket);
                    RegisterPublicationLease();
                    leaseRegistered = true;
                    before = _buffers.SnapshotEntries();
                    var next = new Dictionary<string, XRDataBuffer>(before, StringComparer.Ordinal);

                    foreach (string removal in preparedRemovals)
                        next.Remove(removal);
                    foreach (KeyValuePair<string, XRDataBuffer> replacement in preparedReplacements)
                    {
                        replacement.Value.EnsureOwnerFirstConstructionCompleted();
                        replacement.Value.IsMeshOwnedBuffer = true;
                        next[replacement.Key] = replacement.Value;
                    }

                    // Mark first so a callback that mutates only part of the
                    // aggregate state and then throws is still compensated.
                    stateInstalled = installState is not null;
                    installState?.Invoke();
                    KeyValuePair<string, XRDataBuffer>[] after = [.. next];
                    if (!SameEntries(before, after))
                    {
                        long callbackStart = Stopwatch.GetTimestamp();
                        try
                        {
                            // Keep the attempted state installed until the outer
                            // transaction has restored its matching metadata. If an
                            // observer fails, the catch path below restores metadata
                            // first and only then emits inverse collection callbacks.
                            _buffers.ReplaceContentsNonVetoable(after);
                            _mutationRevision++;
                        }
                        finally
                        {
                            XRMeshCpuPreparationTelemetry.RecordBufferCallback(Stopwatch.GetTimestamp() - callbackStart);
                        }
                    }

                    // A synchronous observer can request destruction on this same
                    // thread. Destroy() converts that request to deferred teardown
                    // while the lease is active; reject the just-installed state and
                    // restore it before returning a durable batch token.
                    ThrowIfOwnerUnavailableForPublication();

                    return new PreparedBufferBatch(
                        this,
                        before,
                        after,
                        Environment.CurrentManagedThreadId);
                }
                catch
                {
                    if (!_retired)
                    {
                        if (before is not null && !SameEntries(_buffers.SnapshotEntries(), before))
                        {
                            try
                            {
                                _buffers.ReplaceContentsNonVetoable(before);
                            }
                            catch (Exception rollbackException)
                            {
                                RuntimeRenderingHostServices.Diagnostics.LogException(
                                    rollbackException,
                                    "Prepared mesh-buffer collection rollback failed.");
                            }
                            _mutationRevision++;
                        }

                        if (stateInstalled)
                        {
                            try
                            {
                                restoreStateOnFailure?.Invoke();
                            }
                            catch (Exception rollbackException)
                            {
                                RuntimeRenderingHostServices.Diagnostics.LogException(
                                    rollbackException,
                                    "Prepared mesh-buffer state rollback failed.");
                            }
                        }
                    }

                    if (leaseRegistered)
                        UnregisterPublicationLease();
                    Monitor.Exit(_mutationLock);
                    throw;
                }
            }

            /// <summary>
            /// Restores a previously swapped batch as one observable state change. This is
            /// safe to call after a render or object-cache publication failure.
            /// </summary>
            internal void RestorePreparedBatch(
                PreparedBufferBatch batch,
                Action? restoreState = null)
            {
                ArgumentNullException.ThrowIfNull(batch);
                ValidatePreparedBatchOwnerAndThread(batch);
                Exception? failure = null;
                try
                {
                    lock (_mutationLock)
                    {
                        if (_retired)
                        {
                            batch.Restored = true;
                            return;
                        }

                        try
                        {
                            restoreState?.Invoke();
                        }
                        catch (Exception exception)
                        {
                            failure = exception;
                        }

                        if (!batch.Restored && !SameEntries(_buffers.SnapshotEntries(), batch.Before))
                        {
                            try
                            {
                                // The dictionary compensation is terminal for this failed
                                // publication: state callbacks may fail, but must not leave
                                // the collection pointing at objects the scope will retire.
                                _buffers.ReplaceContentsNonVetoable(batch.Before);
                            }
                            catch (Exception exception)
                            {
                                failure ??= exception;
                            }
                            finally
                            {
                                _mutationRevision++;
                            }
                        }

                        batch.Restored = true;
                    }
                }
                finally
                {
                    ReleasePreparedBatchLease(batch);
                }

                if (failure is not null)
                    ExceptionDispatchInfo.Capture(failure).Throw();
            }

            /// <summary>
            /// Releases only old owned buffers no longer present in the committed state.
            /// Invoke this after the enclosing publication scope returns successfully.
            /// </summary>
            internal void DisposeReplacedBuffers(PreparedBufferBatch batch)
            {
                ArgumentNullException.ThrowIfNull(batch);
                ValidatePreparedBatchOwnerAndThread(batch);
                try
                {
                    if (batch.Restored || batch.Disposed)
                        return;

                    List<XRDataBuffer> replaced = [];
                    lock (_mutationLock)
                    {
                        KeyValuePair<string, XRDataBuffer>[] current = _buffers.SnapshotEntries();
                        foreach (KeyValuePair<string, XRDataBuffer> entry in batch.Before)
                            if (!ContainsReference(current, entry.Value) &&
                                !replaced.Any(value => ReferenceEquals(value, entry.Value)))
                            {
                                replaced.Add(entry.Value);
                            }
                        batch.Disposed = true;
                    }

                    // Keep the publication lease held while retiring the displaced
                    // buffers so another thread cannot reattach one between the
                    // reference check above and destruction.
                    foreach (XRDataBuffer buffer in replaced)
                        DisposeOwnedBuffer(buffer);
                }
                finally
                {
                    ReleasePreparedBatchLease(batch);
                }
            }

            internal void ValidatePreparedBatchStillPublishable(PreparedBufferBatch batch)
            {
                ArgumentNullException.ThrowIfNull(batch);
                ValidatePreparedBatchOwnerAndThread(batch);
                lock (_mutationLock)
                    ThrowIfOwnerUnavailableForPublication();
            }

            internal sealed class PreparedBufferBatch
            {
                internal PreparedBufferBatch(
                    BufferCollection owner,
                    KeyValuePair<string, XRDataBuffer>[] before,
                    KeyValuePair<string, XRDataBuffer>[] after,
                    int leaseThreadId)
                {
                    Owner = owner;
                    Before = before;
                    After = after;
                    LeaseThreadId = leaseThreadId;
                }

                internal BufferCollection Owner { get; }
                internal KeyValuePair<string, XRDataBuffer>[] Before { get; }
                internal KeyValuePair<string, XRDataBuffer>[] After { get; }
                internal int LeaseThreadId { get; }
                internal bool LeaseHeld { get; set; } = true;
                internal bool Restored { get; set; }
                internal bool Disposed { get; set; }
            }

            internal sealed class PreparedBufferTicket
            {
                internal PreparedBufferTicket(
                    BufferCollection collection,
                    XRMesh? owner,
                    KeyValuePair<string, XRDataBuffer?>[] entries,
                    long? geometryRevision)
                {
                    Collection = collection;
                    Owner = owner;
                    Entries = entries;
                    GeometryRevision = geometryRevision;
                }

                internal BufferCollection Collection { get; }
                internal XRMesh? Owner { get; }
                internal KeyValuePair<string, XRDataBuffer?>[] Entries { get; }
                internal long? GeometryRevision { get; }
            }

            private void ValidatePreparedBatchOwnerAndThread(PreparedBufferBatch batch)
            {
                if (!ReferenceEquals(batch.Owner, this))
                    throw new InvalidOperationException("A prepared mesh-buffer batch was completed by a different collection.");
                if (batch.LeaseThreadId != Environment.CurrentManagedThreadId)
                    throw new InvalidOperationException("Prepared mesh-buffer batches must complete on their publication thread.");
            }

            private void ReleasePreparedBatchLease(PreparedBufferBatch batch)
            {
                if (!batch.LeaseHeld)
                    return;
                ValidatePreparedBatchOwnerAndThread(batch);

                batch.LeaseHeld = false;
                UnregisterPublicationLease();
                Monitor.Exit(_mutationLock);
            }

            private void RegisterPublicationLease()
            {
                int threadId = Environment.CurrentManagedThreadId;
                if (_publicationLeaseDepth == 0)
                    _publicationLeaseThreadId = threadId;
                else if (_publicationLeaseThreadId != threadId)
                    throw new InvalidOperationException("Mesh buffer publication leases are thread-affine.");
                _publicationLeaseDepth++;
            }

            private void UnregisterPublicationLease()
            {
                if (_publicationLeaseDepth <= 0 ||
                    _publicationLeaseThreadId != Environment.CurrentManagedThreadId)
                {
                    throw new InvalidOperationException("Mesh buffer publication lease ownership was lost.");
                }

                _publicationLeaseDepth--;
                if (_publicationLeaseDepth == 0)
                    _publicationLeaseThreadId = 0;
            }

            private void ValidatePreparationTicket(PreparedBufferTicket? ticket)
            {
                if (ticket is null)
                    return;
                if (!ReferenceEquals(ticket.Collection, this) ||
                    !ReferenceEquals(ticket.Owner, _owner))
                {
                    throw new InvalidOperationException("The mesh buffer owner changed while replacements were being prepared.");
                }
                if (ticket.GeometryRevision.HasValue &&
                    _owner?.GeometryRevision != ticket.GeometryRevision.Value)
                {
                    throw new InvalidOperationException("Mesh geometry changed while its buffers were being prepared.");
                }

                foreach (KeyValuePair<string, XRDataBuffer?> expected in ticket.Entries)
                {
                    _buffers.TryGetValue(expected.Key, out XRDataBuffer? current);
                    if (!ReferenceEquals(current, expected.Value))
                    {
                        throw new InvalidOperationException(
                            $"Mesh buffer group containing '{expected.Key}' changed while its replacement was being prepared.");
                    }
                }
            }

            private void ThrowIfOwnerUnavailableForPublication()
            {
                if (_ownerDestructionPending ||
                    _owner?.IsDestroyQueued == true ||
                    _owner?.IsDestroyed == true)
                {
                    throw new ObjectDisposedException(
                        nameof(BufferCollection),
                        "The mesh buffer owner entered teardown during publication.");
                }
            }

            private static bool SameEntries(
                IReadOnlyList<KeyValuePair<string, XRDataBuffer>> left,
                IReadOnlyList<KeyValuePair<string, XRDataBuffer>> right)
                => left.Count == right.Count && left.All(
                    entry => right.Any(candidate =>
                        string.Equals(entry.Key, candidate.Key, StringComparison.Ordinal) &&
                        ReferenceEquals(entry.Value, candidate.Value)));

            private static bool ContainsReference(
                IReadOnlyList<KeyValuePair<string, XRDataBuffer>> entries,
                XRDataBuffer value)
                => entries.Any(entry => ReferenceEquals(entry.Value, value));

            //public XRDataBuffer? this[string bindingName]
            //{
            //    get => _buffers.TryGetValue(bindingName, out XRDataBuffer? buffer) ? buffer : null;
            //    set
            //    {
            //        if (value is null)
            //            _buffers.Remove(bindingName);
            //        else if (!_buffers.TryAdd(bindingName, value))
            //            _buffers[bindingName] = value;
            //    }
            //}

            public void RemoveBuffer(string name)
            {
                if (_buffers is null)
                    return;

                XRDataBuffer? buffer;
                lock (_mutationLock)
                {
                    if (!_buffers.TryGetValue(name, out buffer) || !_buffers.Remove(name))
                        return;
                    _mutationRevision++;
                }
                DisposeOwnedBuffer(buffer);
            }

            internal void RemoveBufferIfSame(string name, XRDataBuffer expected)
            {
                XRDataBuffer? removed = null;
                lock (_mutationLock)
                {
                    if (_buffers.TryGetValue(name, out XRDataBuffer? current) &&
                        ReferenceEquals(current, expected) &&
                        _buffers.Remove(name))
                    {
                        removed = current;
                        _mutationRevision++;
                    }
                }

                DisposeOwnedBuffer(removed);
            }

            public XRDataBuffer SetBufferRaw<T>(
                IList<T> bufferData,
                string bindingName,
                bool remap = false,
                bool integral = false,
                bool isMapped = false,
                uint instanceDivisor = 0,
                EBufferTarget target = EBufferTarget.ArrayBuffer) where T : unmanaged
            {
                using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
                PreparedBufferTicket preparationTicket =
                    CapturePreparationTicket([bindingName], includeGeometryRevision: true);
                XRDataBuffer buffer = new(bindingName, target, integral)
                {
                    IsMeshOwnedBuffer = true,
                    InstanceDivisor = instanceDivisor,
                    ShouldMap = isMapped,
                };
                AddOrUpdateBufferRaw(
                    bufferData,
                    bindingName,
                    remap,
                    instanceDivisor,
                    buffer,
                    publication,
                    preparationTicket);
                return buffer;
            }

            public XRDataBuffer SetBuffer<T>(
                IList<T> bufferData,
                string bindingName,
                bool remap = false,
                bool integral = false,
                bool isMapped = false,
                uint instanceDivisor = 0,
                EBufferTarget target = EBufferTarget.ArrayBuffer) where T : unmanaged, IBufferable
            {
                using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
                PreparedBufferTicket preparationTicket =
                    CapturePreparationTicket([bindingName], includeGeometryRevision: true);
                _buffers ??= [];
                XRDataBuffer buffer = new(bindingName, target, integral)
                {
                    IsMeshOwnedBuffer = true,
                    InstanceDivisor = instanceDivisor,
                    ShouldMap = isMapped
                };
                AddOrUpdateBuffer(
                    bufferData,
                    bindingName,
                    remap,
                    instanceDivisor,
                    buffer,
                    publication,
                    preparationTicket);
                return buffer;
            }

            public Remapper? GetBuffer<T>(string bindingName, out T[]? array, bool remap = false) where T : unmanaged, IBufferable
            {
                array = null;
                XRDataBuffer? buffer;
                lock (_mutationLock)
                    _buffers.TryGetValue(bindingName, out buffer);
                return buffer is not null ? buffer.GetData(out array, remap) : null;
            }

            public Remapper? GetBufferRaw<T>(string bindingName, out T[]? array, bool remap = false) where T : unmanaged
            {
                array = null;
                XRDataBuffer? buffer;
                lock (_mutationLock)
                    _buffers.TryGetValue(bindingName, out buffer);
                return buffer is not null ? buffer.GetDataRaw(out array, remap) : null;
            }

            private void AddOrUpdateBufferRaw<T>(
                IList<T> bufferData,
                string bindingName,
                bool remap,
                uint instanceDivisor,
                XRDataBuffer buffer,
                RenderObjectPublicationScope publication,
                PreparedBufferTicket preparationTicket) where T : unmanaged
            {
                var remapper = buffer.SetDataRaw(bufferData, remap);
                CommitPreparedBuffer(
                    bufferData.Count,
                    bindingName,
                    remap,
                    instanceDivisor,
                    buffer,
                    remapper,
                    publication,
                    preparationTicket);
            }

            private void AddOrUpdateBuffer<T>(
                IList<T> bufferData,
                string bindingName,
                bool remap,
                uint instanceDivisor,
                XRDataBuffer buffer,
                RenderObjectPublicationScope publication,
                PreparedBufferTicket preparationTicket) where T : unmanaged, IBufferable
            {
                var remapper = buffer.SetDataRaw(bufferData, remap);
                CommitPreparedBuffer(
                    bufferData.Count,
                    bindingName,
                    remap,
                    instanceDivisor,
                    buffer,
                    remapper,
                    publication,
                    preparationTicket);
            }

            private void CommitPreparedBuffer(
                int elementCount,
                string bindingName,
                bool remap,
                uint instanceDivisor,
                XRDataBuffer buffer,
                Remapper? remapper,
                RenderObjectPublicationScope publication,
                PreparedBufferTicket preparationTicket)
            {
                PreparedBufferBatch? swap = null;
                publication.Complete(
                    () =>
                    {
                        swap = SwapPreparedBatch(
                            [new KeyValuePair<string, XRDataBuffer>(bindingName, buffer)],
                            removals: null,
                            expectedTicket: preparationTicket);
                        if (buffer.Target == EBufferTarget.ArrayBuffer)
                            UpdateFaceIndices?.Invoke(elementCount, bindingName, remap, instanceDivisor, remapper);
                        ValidatePreparedBatchStillPublishable(swap);
                    },
                    () =>
                    {
                        if (swap is not null)
                            RestorePreparedBatch(swap);
                    },
                    () =>
                    {
                        if (swap is not null)
                            DisposeReplacedBuffers(swap);
                    });
            }

            public delegate void DelUpdateFaceIndices(int count, string bindingName, bool remap, uint instanceDivisor, Remapper? remapper);
            public event DelUpdateFaceIndices? UpdateFaceIndices;

            public event EventDictionary<string, XRDataBuffer>.DelAdded? Added
            {
                add => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Added += value;
                remove => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Added -= value;
            }

            public event EventDictionary<string, XRDataBuffer>.DelCleared? Cleared
            {
                add => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Cleared += value;
                remove => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Cleared -= value;
            }

            public event EventDictionary<string, XRDataBuffer>.DelRemoved? Removed
            {
                add => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Removed += value;
                remove => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Removed -= value;
            }

            public event EventDictionary<string, XRDataBuffer>.DelSet? Set
            {
                add => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Set += value;
                remove => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Set -= value;
            }

            public event Action? Changed
            {
                add => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Changed += value;
                remove => ((IReadOnlyEventDictionary<string, XRDataBuffer>)_buffers).Changed -= value;
            }

            public void Add(string key, XRDataBuffer value)
            {
                value.EnsureOwnerFirstConstructionCompleted();
                value.IsMeshOwnedBuffer = true;
                long callbackStart = Stopwatch.GetTimestamp();
                try
                {
                    lock (_mutationLock)
                    {
                        ThrowIfRetired();
                        ((IDictionary<string, XRDataBuffer>)_buffers).Add(key, value);
                        _mutationRevision++;
                    }
                }
                finally
                {
                    XRMeshCpuPreparationTelemetry.RecordBufferCallback(Stopwatch.GetTimestamp() - callbackStart);
                }
            }
            public bool ContainsKey(string key)
            {
                lock (_mutationLock)
                    return ((IDictionary<string, XRDataBuffer>)_buffers).ContainsKey(key);
            }
            public bool Remove(string key)
            {
                lock (_mutationLock)
                {
                    ThrowIfRetired();
                    bool removed = ((IDictionary<string, XRDataBuffer>)_buffers).Remove(key);
                    if (removed)
                        _mutationRevision++;
                    return removed;
                }
            }
            public bool TryGetValue(string key, [MaybeNullWhen(false)] out XRDataBuffer value)
            {
                lock (_mutationLock)
                    return ((IDictionary<string, XRDataBuffer>)_buffers).TryGetValue(key, out value);
            }
            public void Add(KeyValuePair<string, XRDataBuffer> item) => Add(item.Key, item.Value);
            public void Clear()
            {
                XRDataBuffer[] ownedBuffers;
                lock (_mutationLock)
                {
                    ThrowIfRetired();
                    ownedBuffers = _ownsBufferData ? [.. _buffers.Values] : [];
                    ((ICollection<KeyValuePair<string, XRDataBuffer>>)_buffers).Clear();
                    _mutationRevision++;
                }
                for (int index = 0; index < ownedBuffers.Length; index++)
                    DisposeOwnedBuffer(ownedBuffers[index]);
            }
            public bool Contains(KeyValuePair<string, XRDataBuffer> item)
            {
                lock (_mutationLock)
                    return ((ICollection<KeyValuePair<string, XRDataBuffer>>)_buffers).Contains(item);
            }
            public void CopyTo(KeyValuePair<string, XRDataBuffer>[] array, int arrayIndex)
                => SnapshotBuffers().CopyTo(array, arrayIndex);
            public bool Remove(KeyValuePair<string, XRDataBuffer> item)
            {
                bool removed;
                lock (_mutationLock)
                {
                    removed = ((ICollection<KeyValuePair<string, XRDataBuffer>>)_buffers).Remove(item);
                    if (removed)
                        _mutationRevision++;
                }
                return removed;
            }
            public void Add(object key, object? value)
            {
                if (key is not string bindingName || value is not XRDataBuffer buffer)
                    throw new ArgumentException("Mesh buffer entries require a string key and XRDataBuffer value.");
                Add(bindingName, buffer);
            }
            public bool Contains(object key)
            {
                lock (_mutationLock)
                    return ((IDictionary)_buffers).Contains(key);
            }
            public IEnumerator<KeyValuePair<string, XRDataBuffer>> GetEnumerator()
                => ((IEnumerable<KeyValuePair<string, XRDataBuffer>>)SnapshotBuffers()).GetEnumerator();
            public void Remove(object key)
            {
                if (key is string bindingName)
                    Remove(bindingName);
            }
            public void CopyTo(Array array, int index) => ((ICollection)SnapshotBuffers()).CopyTo(array, index);
#pragma warning disable SYSLIB0050
            public void GetObjectData(SerializationInfo info, StreamingContext context) => ((ISerializable)_buffers).GetObjectData(info, context);
#pragma warning restore SYSLIB0050
            public void OnDeserialization(object? sender) => ((IDeserializationCallback)_buffers).OnDeserialization(sender);
            IEnumerator<KeyValuePair<string, XRDataBuffer>> IEnumerable<KeyValuePair<string, XRDataBuffer>>.GetEnumerator() => GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => SnapshotBuffers().GetEnumerator();

            public BufferCollection Clone()
            {
                using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
                BufferCollection clone = new(ownsBufferData: true);
                foreach (KeyValuePair<string, XRDataBuffer> kvp in SnapshotBuffers())
                    clone.Add(kvp.Key, kvp.Value.Clone(true, kvp.Value.Target));
                publication.Complete();
                return clone;
            }

            internal BufferCollection CloneShared()
            {
                BufferCollection clone = new(ownsBufferData: false);
                foreach (KeyValuePair<string, XRDataBuffer> kvp in SnapshotBuffers())
                    clone.Add(kvp.Key, kvp.Value);
                return clone;
            }

            internal void RetireAndDisposeOwnedBuffers()
            {
                List<XRDataBuffer> ownedBuffers = [];
                lock (_mutationLock)
                {
                    if (_retired)
                        return;

                    _retired = true;
                    _owner = null;
                    if (_ownsBufferData)
                    {
                        foreach (XRDataBuffer buffer in _buffers.Values)
                            if (!ownedBuffers.Any(value => ReferenceEquals(value, buffer)))
                                ownedBuffers.Add(buffer);
                    }

                    try
                    {
                        _buffers.ReplaceContentsNonVetoable([]);
                    }
                    catch (Exception ex)
                    {
                        // Terminal teardown is non-vetoable. The collection already
                        // contains the requested empty state when this callback fails.
                        RuntimeRenderingHostServices.Diagnostics.LogException(
                            ex,
                            "Mesh buffer retirement observer failed.");
                    }
                    _mutationRevision++;
                }

                foreach (XRDataBuffer buffer in ownedBuffers)
                    DisposeOwnedBuffer(buffer);
            }

            private void SetOwnedBuffer(string key, XRDataBuffer value)
            {
                XRDataBuffer? previous = SwapOwnedBuffer(
                    key,
                    value,
                    expectedMutationRevision: null,
                    expectedGeometryRevision: null);

                if (!ReferenceEquals(previous, value))
                    DisposeOwnedBuffer(previous);
            }

            private XRDataBuffer? SwapOwnedBuffer(
                string key,
                XRDataBuffer value,
                long? expectedMutationRevision,
                long? expectedGeometryRevision)
            {
                value.EnsureOwnerFirstConstructionCompleted();
                value.IsMeshOwnedBuffer = true;
                long callbackStart = Stopwatch.GetTimestamp();
                try
                {
                    lock (_mutationLock)
                    {
                        ThrowIfRetired();
                        if (expectedMutationRevision.HasValue &&
                            _mutationRevision != expectedMutationRevision.Value)
                        {
                            throw new InvalidOperationException(
                                $"Mesh buffer '{key}' changed while its replacement was being prepared.");
                        }
                        if (expectedGeometryRevision.HasValue &&
                            _owner?.GeometryRevision != expectedGeometryRevision.Value)
                        {
                            throw new InvalidOperationException(
                                $"Mesh geometry changed while buffer '{key}' was being prepared.");
                        }

                        _buffers.TryGetValue(key, out XRDataBuffer? previous);
                        try
                        {
                            if (previous is null)
                                _buffers.Add(key, value);
                            else
                                _buffers[key] = value;
                            _mutationRevision++;
                            return previous;
                        }
                        catch
                        {
                            RestoreOwnedBufferUnderLock(key, value, previous);
                            _mutationRevision++;
                            throw;
                        }
                    }
                }
                finally
                {
                    XRMeshCpuPreparationTelemetry.RecordBufferCallback(Stopwatch.GetTimestamp() - callbackStart);
                }
            }

            private void ValidateExpectedMutationRevision(
                long? expectedMutationRevision,
                long? expectedGeometryRevision)
            {
                ThrowIfRetired();
                if (expectedMutationRevision.HasValue &&
                    _mutationRevision != expectedMutationRevision.Value)
                {
                    throw new InvalidOperationException(
                        "Mesh buffers changed while their replacement was being prepared.");
                }
                if (expectedGeometryRevision.HasValue &&
                    _owner?.GeometryRevision != expectedGeometryRevision.Value)
                {
                    throw new InvalidOperationException(
                        "Mesh geometry changed while its buffers were being prepared.");
                }
            }

            private void RestoreOwnedBuffer(string key, XRDataBuffer attempted, XRDataBuffer? previous)
            {
                lock (_mutationLock)
                {
                    RestoreOwnedBufferUnderLock(key, attempted, previous);
                    _mutationRevision++;
                }
            }

            private void RestoreOwnedBufferUnderLock(string key, XRDataBuffer attempted, XRDataBuffer? previous)
            {
                try
                {
                    if (!_buffers.TryGetValue(key, out XRDataBuffer? current) || !ReferenceEquals(current, attempted))
                        return;

                    if (previous is null)
                        _buffers.Remove(key);
                    else
                        _buffers[key] = previous;
                }
                catch
                {
                    // Preserve the original construction or callback failure.
                }
            }

            private void DisposeOwnedBuffer(XRDataBuffer? value)
            {
                if (!_ownsBufferData || value is null)
                    return;

                value.Destroy(now: true);
                value.Dispose();
            }

            private void ThrowIfRetired()
            {
                if (_retired)
                    throw new ObjectDisposedException(nameof(BufferCollection), "The mesh buffer owner has entered terminal teardown.");
                ThrowIfOwnerUnavailableForPublication();
            }

            private KeyValuePair<string, XRDataBuffer>[] SnapshotBuffers()
            {
                lock (_mutationLock)
                    return _buffers.SnapshotEntries();
            }

            public EventDictionary<string, XRDataBuffer> Buffers
            {
                get => _buffers;
                set => SetField(ref _buffers, value);
            }
            public ICollection<string> Keys => SnapshotBuffers().Select(entry => entry.Key).ToArray();
            public ICollection<XRDataBuffer> Values => SnapshotBuffers().Select(entry => entry.Value).ToArray();
            public int Count
            {
                get
                {
                    lock (_mutationLock)
                        return ((ICollection<KeyValuePair<string, XRDataBuffer>>)_buffers).Count;
                }
            }
            public bool IsReadOnly
            {
                get
                {
                    lock (_mutationLock)
                        return ((ICollection<KeyValuePair<string, XRDataBuffer>>)_buffers).IsReadOnly;
                }
            }
            public bool IsFixedSize
            {
                get
                {
                    lock (_mutationLock)
                        return ((IDictionary)_buffers).IsFixedSize;
                }
            }
            public bool IsSynchronized => false;
            public object SyncRoot => _mutationLock;
            IEnumerable<string> IReadOnlyDictionary<string, XRDataBuffer>.Keys => Keys;
            IEnumerable<XRDataBuffer> IReadOnlyDictionary<string, XRDataBuffer>.Values => Values;
            XRDataBuffer IReadOnlyDictionary<string, XRDataBuffer>.this[string key] => this[key];
            public XRDataBuffer this[string key]
            {
                get
                {
                    lock (_mutationLock)
                        return ((IDictionary<string, XRDataBuffer>)_buffers)[key];
                }
                set => SetOwnedBuffer(key, value);
            }
            public object? this[object key]
            {
                get
                {
                    lock (_mutationLock)
                        return ((IDictionary)_buffers)[key];
                }
                set
                {
                    if (key is not string bindingName || value is not XRDataBuffer buffer)
                        throw new ArgumentException("Mesh buffer entries require a string key and XRDataBuffer value.");
                    SetOwnedBuffer(bindingName, buffer);
                }
            }
        }

        public bool GetBlendshapeIndex(string name, out uint index)
        {
            index = 0;
            int i = _blendshapeNameToIndex.TryGetValue(name, out int value) ? value : -1;
            if (i < 0)
                return false;
            index = (uint)i;
            return true;
        }
    }
}
