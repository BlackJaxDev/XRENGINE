using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

internal sealed partial class SkinningPrepassDispatcher
{
    // Per-dispatch copies of the renderer-owned skin palette.
    private sealed partial class RendererResources
    {
        /// <summary>
        /// Number of palette copies in the ring. A copy is written again only after this many
        /// later dispatches of the same renderer. This must be more than the frames in flight
        /// plus the frames that a queued dispatch can wait before it is recorded.
        /// </summary>
        private const int SkinPaletteSnapshotCount = 4;

        private readonly XRDataBuffer?[] _skinPaletteSnapshots = new XRDataBuffer?[SkinPaletteSnapshotCount];
        private int _skinPaletteSnapshotCursor;

        /// <summary>
        /// Copies the current palette into the next ring buffer and returns that buffer for the dispatch.
        /// </summary>
        /// <remarks>
        /// The renderer palette is one host-visible buffer. The CPU writes it when the frame is
        /// built, but the GPU reads it only when the queued dispatch runs, which can be one or
        /// more frames later. A direct bind lets a later frame overwrite the pose before an
        /// earlier dispatch reads it, so the skinned pose jumps forward at an uneven rate.
        /// Each dispatch therefore reads its own copy, which no frame writes again until that
        /// dispatch is complete.
        /// </remarks>
        public XRDataBuffer PublishSkinPaletteSnapshot(XRDataBuffer source)
        {
            int slot = _skinPaletteSnapshotCursor;
            _skinPaletteSnapshotCursor = (slot + 1) % SkinPaletteSnapshotCount;

            XRDataBuffer? snapshot = _skinPaletteSnapshots[slot];
            if (snapshot is null ||
                snapshot.ElementCount != source.ElementCount ||
                snapshot.ComponentCount != source.ComponentCount ||
                snapshot.ComponentType != source.ComponentType)
            {
                snapshot?.Destroy();
                snapshot = new XRDataBuffer(
                    $"{ECommonBufferType.SkinPalette}Snapshot{slot}",
                    EBufferTarget.ShaderStorageBuffer,
                    source.ElementCount,
                    source.ComponentType,
                    source.ComponentCount,
                    source.Normalize,
                    source.Integral)
                {
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false,
                };
                _skinPaletteSnapshots[slot] = snapshot;
            }

            uint length = Math.Min(source.Length, snapshot.Length);
            if (length > 0u && source.Address != VoidPtr.Zero && snapshot.Address != VoidPtr.Zero)
            {
                unsafe
                {
                    System.Buffer.MemoryCopy(source.Address.Pointer, snapshot.Address.Pointer, snapshot.Length, length);
                }
                snapshot.CommitDirtyBytes(0u, length);
            }

            return snapshot;
        }

        private void DestroySkinPaletteSnapshots()
        {
            for (int i = 0; i < _skinPaletteSnapshots.Length; i++)
            {
                _skinPaletteSnapshots[i]?.Destroy();
                _skinPaletteSnapshots[i] = null;
            }
            _skinPaletteSnapshotCursor = 0;
        }
    }
}
