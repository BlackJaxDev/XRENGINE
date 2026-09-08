using System.Threading;
using XREngine;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Components.Capture.Lights;

/// <summary>
/// Immutable pair of octahedral IBL outputs. The producer owns a generation
/// until it is superseded; publication snapshots retain it independently.
/// </summary>
public sealed class LightProbeIblOutputGeneration : IAdvancedGpuPublicationSourceLifetime
{
    private readonly object _sync = new();
    private XRGpuFence? _writerFence;
    private bool _producerOwned = true;
    private bool _destroyQueued;
    private bool _retirementScheduled;
    private bool _outputValid;
    private int _publicationReferences;

    public LightProbeIblOutputGeneration(uint generation, XRTexture2D irradiance, XRTexture2D prefilteredRadiance)
    {
        Generation = generation;
        Irradiance = irradiance ?? throw new ArgumentNullException(nameof(irradiance));
        PrefilteredRadiance = prefilteredRadiance ?? throw new ArgumentNullException(nameof(prefilteredRadiance));
    }

    public uint Generation { get; }
    public XRTexture2D Irradiance { get; }
    public XRTexture2D PrefilteredRadiance { get; }
    public bool OutputValid => _outputValid;

    public void MarkOutputValid()
        => _outputValid = true;

    public void ArmWriterFence(XRGpuFence? fence)
    {
        lock (_sync)
        {
            if (_writerFence is not null)
                throw new InvalidOperationException("An IBL output generation already has a writer fence.");
            _writerFence = fence;
        }
    }

    public bool IsWriterComplete()
    {
        lock (_sync)
        {
            if (_writerFence is null || _writerFence.SubmissionStatus != EGpuFenceSubmissionStatus.Submitted)
                return false;
            return _writerFence.Poll() == EGpuFenceStatus.Signaled;
        }
    }

    public bool IsWriterRejected()
    {
        lock (_sync)
            return _writerFence is null ||
                _writerFence.SubmissionStatus == EGpuFenceSubmissionStatus.Failed ||
                (_writerFence.SubmissionStatus == EGpuFenceSubmissionStatus.Submitted && _writerFence.Poll() == EGpuFenceStatus.Failed);
    }

    /// <summary>A failed submitted fence provides no safe completion proof for the producer closure.</summary>
    public bool IsSubmittedWriterUncertain()
    {
        lock (_sync)
            return _writerFence?.SubmissionStatus == EGpuFenceSubmissionStatus.Submitted &&
                _writerFence.Poll() == EGpuFenceStatus.Failed;
    }

    public bool TryRetainPublication()
    {
        lock (_sync)
        {
            if (_destroyQueued)
                return false;
            if (_publicationReferences == int.MaxValue)
                return false;
            ++_publicationReferences;
            return true;
        }
    }

    public void ReleasePublication()
    {
        lock (_sync)
        {
            if (_publicationReferences == 0)
                throw new InvalidOperationException("IBL publication reference underflow.");
            --_publicationReferences;
        }
    }

    public void ReleaseProducer()
    {
        lock (_sync)
        {
            _producerOwned = false;
            if (_retirementScheduled)
                return;
            _retirementScheduled = true;
            RuntimeEngine.AddRenderThreadCoroutine(
                DrainRetirement,
                "LightProbeIblOutputGeneration.Retire",
                RenderThreadJobKind.RenderPipelineResource);
        }
    }

    private bool DrainRetirement()
    {
        XRGpuFence? fence;
        lock (_sync)
        {
            if (_destroyQueued)
                return true;
            if (_producerOwned || _publicationReferences != 0 || _writerFence is null)
                return false;

            EGpuFenceSubmissionStatus submission = _writerFence.SubmissionStatus;
            if (submission == EGpuFenceSubmissionStatus.AwaitingSubmission)
                return false;
            if (submission == EGpuFenceSubmissionStatus.Submitted && _writerFence.Poll() == EGpuFenceStatus.Pending)
                return false;

            _destroyQueued = true;
            fence = _writerFence;
            _writerFence = null;
        }

        Irradiance.Destroy();
        PrefilteredRadiance.Destroy();
        fence.Dispose();
        return true;
    }

    /// <summary>Discards a generation only after its complete producer batch was rolled back before submission.</summary>
    public void DiscardUnwritten()
    {
        lock (_sync)
        {
            if (_writerFence is not null || _publicationReferences != 0)
                throw new InvalidOperationException("A written or published IBL generation cannot be discarded as unwritten.");
            _producerOwned = false;
            _destroyQueued = true;
        }
        Irradiance.Destroy();
        PrefilteredRadiance.Destroy();
    }
}
