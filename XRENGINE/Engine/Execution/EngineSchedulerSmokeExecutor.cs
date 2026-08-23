using XREngine.Execution;

namespace XREngine;

/// <summary>
/// Renderer-neutral startup proof for disjoint preparation ranges.
/// </summary>
internal sealed class EngineSchedulerSmokeExecutor : IRenderWorkExecutor
{
    private readonly int[] _output;

    internal EngineSchedulerSmokeExecutor(int[] output)
    {
        _output = output;
    }

    public void Execute(in RenderWorkItem item, ref RenderWorkerContext context)
    {
        if (item.OperationKind != 1)
            throw new InvalidOperationException($"Unknown scheduler smoke operation {item.OperationKind}.");

        int end = checked(item.SourceStart + item.SourceCount);
        if ((uint)end > (uint)_output.Length)
            throw new InvalidOperationException("Scheduler smoke range exceeds its output buffer.");

        for (int index = item.SourceStart; index < end; index++)
            _output[index] = unchecked(((index + 1) * 31) ^ 0x5A5A);
    }
}
