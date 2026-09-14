using System.Threading;

namespace XREngine.Rendering;

/// <summary>Internal immutable shader identity and independently atomic command counters.</summary>
internal sealed class ShaderCommandCoverageEntry(
    string backend,
    string language,
    string stage,
    string entryPoint,
    string sourceIdentity,
    string authoredSourceSha256,
    long sourceRevision)
{
    private long _graphicsDirect;
    private long _graphicsIndirect;
    private long _computeDirect;
    private long _computeIndirect;
    private long _knownInstanced;
    private long _knownNonInstanced;
    private long _unknownInstancing;

    public void Record(ShaderCommandKind kind, bool indirect, bool instancingKnown, bool instanced)
    {
        if (kind == ShaderCommandKind.Graphics)
        {
            if (indirect)
                Interlocked.Increment(ref _graphicsIndirect);
            else
                Interlocked.Increment(ref _graphicsDirect);
        }
        else if (indirect)
            Interlocked.Increment(ref _computeIndirect);
        else
            Interlocked.Increment(ref _computeDirect);

        if (kind != ShaderCommandKind.Graphics)
            return;
        if (!instancingKnown)
            Interlocked.Increment(ref _unknownInstancing);
        else if (instanced)
            Interlocked.Increment(ref _knownInstanced);
        else
            Interlocked.Increment(ref _knownNonInstanced);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _graphicsDirect, 0);
        Interlocked.Exchange(ref _graphicsIndirect, 0);
        Interlocked.Exchange(ref _computeDirect, 0);
        Interlocked.Exchange(ref _computeIndirect, 0);
        Interlocked.Exchange(ref _knownInstanced, 0);
        Interlocked.Exchange(ref _knownNonInstanced, 0);
        Interlocked.Exchange(ref _unknownInstancing, 0);
    }

    public ShaderCommandCoverageEntrySnapshot Snapshot() => new(
        backend, backend == "Vulkan" ? "Recorded" : "Issued", language, stage, entryPoint, sourceIdentity, authoredSourceSha256, sourceRevision,
        Interlocked.Read(ref _graphicsDirect), Interlocked.Read(ref _graphicsIndirect),
        Interlocked.Read(ref _computeDirect), Interlocked.Read(ref _computeIndirect), Interlocked.Read(ref _knownInstanced),
        Interlocked.Read(ref _knownNonInstanced), Interlocked.Read(ref _unknownInstancing));
}
