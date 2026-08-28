using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XREngine.Execution;

/// <summary>
/// CPU-visible diagnostic words whose producing GPU operation has already
/// completed before construction. The receipt deliberately cannot carry a
/// fence, task, wait handle, or polling callback, so pending GPU completion can
/// never occupy a general worker item.
/// </summary>
public readonly record struct CompletedDiagnosticPayload
{
    public CompletedDiagnosticPayload(
        ArraySegment<uint> words,
        ulong sourceFrameId,
        long completionTimestamp,
        uint decoderId = 0u,
        Action<CompletedDiagnosticPayload>? semanticDecoder = null)
    {
        Words = words;
        SourceFrameId = sourceFrameId;
        CompletionTimestamp = completionTimestamp;
        DecoderId = decoderId;
        SemanticDecoder = semanticDecoder;
    }

    /// <summary>
    /// Array-backed words. Keeping the segment, rather than the source memory
    /// manager, makes span access a nonblocking CPU operation by construction.
    /// </summary>
    public ArraySegment<uint> Words { get; }

    public ulong SourceFrameId { get; }

    public long CompletionTimestamp { get; }

    /// <summary>Renderer-owned stable decoder identity; zero means checksum-only.</summary>
    public uint DecoderId { get; }

    /// <summary>
    /// Optional CPU-only semantic decoder. The completed-payload job invokes it
    /// on the general/telemetry domain after checksum decoding; it must never
    /// wait for GPU work or retain the backing array.
    /// </summary>
    public Action<CompletedDiagnosticPayload>? SemanticDecoder { get; }

    public static CompletedDiagnosticPayload Create(
        ReadOnlyMemory<uint> completedWords,
        ulong sourceFrameId = 0,
        uint decoderId = 0u,
        Action<CompletedDiagnosticPayload>? semanticDecoder = null)
    {
        if (!MemoryMarshal.TryGetArray(completedWords, out ArraySegment<uint> arrayWords))
        {
            throw new ArgumentException(
                "Completed diagnostic payloads must be array-backed before publication.",
                nameof(completedWords));
        }

        return new CompletedDiagnosticPayload(
            arrayWords,
            sourceFrameId,
            Stopwatch.GetTimestamp(),
            decoderId,
            semanticDecoder);
    }
}
