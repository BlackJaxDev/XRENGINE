using XREngine.Rendering.Info;

namespace XREngine.Rendering.Shadows;

/// <summary>
/// One diagnostic capture of the shadow-only caster candidates that one main-view GPU collection counts.
/// </summary>
/// <remarks>
/// <para>
/// The diagnostic thread creates the capture through
/// <see cref="XREngine.Scene.VisualScene3D.RequestShadowOnlyCasterCensusCapture"/>, which allocates the record buffer.
/// The next main-view GPU collection that starts claims the capture, records up to <see cref="Capacity"/> candidates,
/// and completes the capture when the collection completes. A collection that does not complete releases its claim,
/// and the next collection claims the capture again.
/// </para>
/// <para>
/// Only the claiming collection writes records. Each write takes its slot with an interlocked increment, so a write
/// never overwrites another record and never allocates. The completion is a release write, so read the records only
/// after <see cref="IsCompleted"/> returns true.
/// </para>
/// </remarks>
public sealed class ShadowOnlyCasterCensusCapture
{
    private const int StateArmed = 0;
    private const int StateRecording = 1;
    private const int StateCompleted = 2;

    private readonly ShadowOnlyCasterCandidateRecord[] _records;
    private int _state;
    private int _nextSlot;
    private int _candidateCount;
    private IRuntimeCullingCamera? _camera;
    private bool _collectMirrors;

    internal ShadowOnlyCasterCensusCapture(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _records = new ShadowOnlyCasterCandidateRecord[capacity];
    }

    /// <summary>Gets the maximum number of candidates that the capture records.</summary>
    public int Capacity => _records.Length;

    /// <summary>Gets whether a main-view collection completed and published the capture.</summary>
    public bool IsCompleted => Volatile.Read(ref _state) == StateCompleted;

    /// <summary>
    /// Gets the number of candidates that the captured collection counted. This number can be larger than
    /// <see cref="RecordedCount"/>. Valid only after <see cref="IsCompleted"/> returns true.
    /// </summary>
    public int CandidateCount => IsCompleted ? _candidateCount : 0;

    /// <summary>Gets the number of records. Valid only after <see cref="IsCompleted"/> returns true.</summary>
    public int RecordedCount => IsCompleted ? Math.Min(_nextSlot, _records.Length) : 0;

    /// <summary>Gets the camera of the captured collection. Valid only after <see cref="IsCompleted"/> returns true.</summary>
    public IRuntimeCullingCamera? Camera => IsCompleted ? _camera : null;

    /// <summary>
    /// Gets whether the captured collection included mirror captures. Valid only after <see cref="IsCompleted"/>
    /// returns true.
    /// </summary>
    public bool CollectMirrors => IsCompleted && _collectMirrors;

    /// <summary>Gets the record at <paramref name="index"/>.</summary>
    /// <exception cref="InvalidOperationException">The capture is not complete.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not less than <see cref="RecordedCount"/>.</exception>
    public ShadowOnlyCasterCandidateRecord GetRecord(int index)
    {
        if (!IsCompleted)
            throw new InvalidOperationException("The shadow-only caster census capture is not complete.");
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, RecordedCount);
        return _records[index];
    }

    /// <summary>
    /// Claims the capture for one main-view collection. Only one collection can claim it.
    /// </summary>
    /// <returns>True when this collection must record the candidates and then complete or abandon the capture.</returns>
    internal bool TryBeginRecording(IRuntimeCullingCamera? camera, bool collectMirrors)
    {
        if (Interlocked.CompareExchange(ref _state, StateRecording, StateArmed) != StateArmed)
            return false;

        _camera = camera;
        _collectMirrors = collectMirrors;
        Volatile.Write(ref _nextSlot, 0);
        return true;
    }

    /// <summary>
    /// Records one candidate in the next free slot. When the buffer is full, the candidate counts in
    /// <see cref="CandidateCount"/> only.
    /// </summary>
    /// <param name="renderInfo">The render info that the main view counted.</param>
    /// <param name="reason">Why the main view counted the render info.</param>
    /// <param name="callback">
    /// The pre-collection callback that the main view invoked. For a rejection by
    /// <see cref="RenderInfo3D.AllowRender"/>, the callback that the render info has now.
    /// </param>
    internal void Record(
        RenderInfo3D renderInfo,
        EShadowOnlyCasterCandidateReason reason,
        RenderInfo.DelAddRenderCommandsCallback? callback)
    {
        int slot = Interlocked.Increment(ref _nextSlot) - 1;
        if ((uint)slot >= (uint)_records.Length)
            return;

        _records[slot] = new ShadowOnlyCasterCandidateRecord(
            renderInfo,
            renderInfo.Owner,
            reason,
            callback,
            renderInfo.IsCameraIndependentPreCollectCommandsCallback(callback),
            renderInfo.Layer,
            renderInfo.CastsShadows,
            renderInfo.IsVisible,
            renderInfo.IsDisposalRequested,
            renderInfo.OwnerRenderableMesh?.IsRetirementRequested == true);
    }

    /// <summary>Publishes the records of the completed collection.</summary>
    internal void Complete(int candidateCount)
    {
        _candidateCount = candidateCount;
        Volatile.Write(ref _state, StateCompleted);
    }

    /// <summary>
    /// Releases the claim of a collection that did not complete. The next main-view collection claims the capture
    /// again and records from the first slot.
    /// </summary>
    internal void Abandon()
    {
        _camera = null;
        _collectMirrors = false;
        Volatile.Write(ref _nextSlot, 0);
        Volatile.Write(ref _state, StateArmed);
    }
}
