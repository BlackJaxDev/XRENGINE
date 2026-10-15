namespace XREngine.Rendering.Commands;

/// <summary>
/// Records writes to the command row inputs that the canonical unchanged-row
/// gate reads. A check that runs outside the swap uses the version to prove
/// that the render-side rows equal the rows that the next swap publishes.
/// </summary>
public partial class GPUScene
{
    // Advances after each write to a row input of the canonical unchanged-row
    // gate: an updating draw or bounds row, the source lookup, the command
    // count, the stream capacity, and a command update that delivers a new
    // render snapshot. Every writer holds _lock.
    private long _advancedRowInputVersion;

    // The row-input version immediately after the swap copied the updating
    // rows to the render side. A later write reaches the render side only at
    // the next swap, so it leaves the version newer than this value.
    private long _advancedRowInputVersionAtSwap;

    /// <summary>
    /// Records a write to the inputs of one command row. The version is one
    /// scalar, so a write to any row advances it. Each call site names its
    /// row, so that a per-row record can use the same call sites. The caller
    /// holds the scene lock and calls this after the write.
    /// </summary>
    private void MarkAdvancedRowInputWritten(uint row)
        => ++_advancedRowInputVersion;

    /// <summary>
    /// Records a write that can change any command row: a count change, a
    /// stream resize, or a lookup reset. The caller holds the scene lock and
    /// calls this after the write.
    /// </summary>
    private void MarkAllAdvancedRowInputsWritten()
        => ++_advancedRowInputVersion;

    /// <summary>
    /// Records the row-input version after the swap copied the updating rows
    /// to the render side. The caller holds the scene lock.
    /// </summary>
    private void RecordAdvancedRowInputVersionAtSwap()
        => _advancedRowInputVersionAtSwap = _advancedRowInputVersion;

    /// <summary>
    /// Returns true when no row input changed after the last swap copy. Then
    /// the render-side rows equal the rows that the next swap publishes, and
    /// <paramref name="version"/> identifies that state. The check also needs
    /// equal updating and render command counts and empty draw and bounds
    /// dirty ranges.
    /// </summary>
    internal bool TryCaptureAdvancedRowBaseline(out long version)
    {
        using (_lock.EnterScope())
        {
            version = _advancedRowInputVersion;
            return version == _advancedRowInputVersionAtSwap &&
                _updatingCommandCount == _totalCommandCount &&
                !_drawMetadataDirtyRange.HasValue &&
                !_boundsDirtyRange.HasValue;
        }
    }

    /// <summary>
    /// Returns true when no row input changed after
    /// <see cref="TryCaptureAdvancedRowBaseline"/> gave <paramref name="version"/>.
    /// </summary>
    internal bool IsAdvancedRowInputVersionCurrent(long version)
    {
        using (_lock.EnterScope())
            return _advancedRowInputVersion == version;
    }

    /// <summary>
    /// Reads the source lookup and the render-side rows of one command in one
    /// scene-lock scope. Every resize of the row storage holds the same lock,
    /// so a reader on another thread never reads released row memory.
    /// Returns false when the lookup has no source for the command, or when
    /// the render side has no row for it.
    /// </summary>
    /// <param name="commandIndex">The command row to read.</param>
    /// <param name="readCullBounds">True to also read the render-side cull bounds row.</param>
    /// <param name="source">The source command of the row.</param>
    /// <param name="primitiveIndex">The source primitive of the row.</param>
    /// <param name="command">The render-side draw row.</param>
    /// <param name="cullBounds">
    /// The render-side cull bounds row, or the default value when
    /// <paramref name="readCullBounds"/> is false or the row does not exist.
    /// </param>
    internal bool TryGetAdvancedRowInputs(
        uint commandIndex,
        bool readCullBounds,
        out IRenderCommandMesh? source,
        out int primitiveIndex,
        out DrawMetadata command,
        out BoundsGpu cullBounds)
    {
        using (_lock.EnterScope())
        {
            if (_commandIndexLookup.TryGetValue(commandIndex, out var entry) &&
                TryGetAdvancedPreparationCommand(commandIndex, out command))
            {
                source = entry.command;
                primitiveIndex = entry.subMeshIndex;
                cullBounds = readCullBounds &&
                    _allLoadedBoundsBuffer is { } boundsBuffer &&
                    command.BoundsID < boundsBuffer.ElementCount
                        ? boundsBuffer.GetDataRawAtIndex<BoundsGpu>(command.BoundsID)
                        : default;
                return true;
            }
        }

        source = null;
        primitiveIndex = -1;
        command = default;
        cullBounds = default;
        return false;
    }
}
