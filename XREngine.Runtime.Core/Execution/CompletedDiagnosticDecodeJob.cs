using System.Collections;

namespace XREngine.Execution;

/// <summary>
/// General/telemetry-domain smoke decoder for an already-completed CPU payload.
/// </summary>
public sealed class CompletedDiagnosticDecodeJob : Job
{
    private readonly CompletedDiagnosticPayload _payload;

    public CompletedDiagnosticDecodeJob(in CompletedDiagnosticPayload payload)
    {
        _payload = payload;
    }

    public ulong Checksum { get; private set; }

    public override IEnumerable Process()
    {
        DecodeCompletedWords();
        SetResult(Checksum);
        yield break;
    }

    private void DecodeCompletedWords()
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong checksum = offsetBasis ^ _payload.SourceFrameId;
        ReadOnlySpan<uint> words = _payload.Words.AsSpan();
        for (int index = 0; index < words.Length; index++)
        {
            checksum ^= words[index];
            checksum *= prime;
        }

        Checksum = checksum;
    }
}
