using XREngine.Components.Lights;
using XREngine.Data.Vectors;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>Validated immutable dimensions for every allocation in one DDGI generation.</summary>
internal readonly record struct DDGIResourceDescriptor(IVector3 Counts, int RaysPerProbe, int Cascades, int MaxUpdatedProbes)
{
    private const ulong Schema = 0x444447490001UL;
    public static DDGIResourceDescriptor Default => new(DDGIVolumeRuntimeState.DefaultProbeCounts,
        (int)DDGIVolumeRuntimeState.DefaultRaysPerProbe, 1, 0);

    public uint ProbeCount => checked((uint)(checked(Counts.X * Counts.Y * Counts.Z)));
    public uint ProbeElements => checked(ProbeCount * (uint)Cascades);
    public uint ScheduledCapacity => MaxUpdatedProbes > 0 ? Math.Min((uint)MaxUpdatedProbes, ProbeCount) : ProbeCount;
    public uint RayElements => checked(ScheduledCapacity * (uint)RaysPerProbe);
    public uint IrradianceWidth => checked((uint)Counts.X * DDGIVolumeRuntimeState.IrradianceProbeSize);
    public uint IrradianceHeight => checked((uint)Counts.Y * (uint)Counts.Z * DDGIVolumeRuntimeState.IrradianceProbeSize);
    public uint VisibilityWidth => checked((uint)Counts.X * DDGIVolumeRuntimeState.VisibilityProbeSize);
    public uint VisibilityHeight => checked((uint)Counts.Y * (uint)Counts.Z * DDGIVolumeRuntimeState.VisibilityProbeSize);

    public static DDGIResourceDescriptor FromVolume(DDGIVolumeComponent volume)
    {
        var descriptor = new DDGIResourceDescriptor(volume.ProbeCounts, volume.RaysPerProbe, volume.CascadeCount, volume.MaxProbesUpdatedPerFrame);
        descriptor.Validate();
        return descriptor;
    }

    public RenderPipelineResourceVariant ToVariant()
        => new(Pack((uint)Counts.X, (uint)Counts.Y), Pack((uint)Counts.Z, (uint)RaysPerProbe),
            Pack((uint)Cascades, (uint)MaxUpdatedProbes), Schema);

    public static DDGIResourceDescriptor FromVariant(RenderPipelineResourceVariant variant)
    {
        if (variant == default)
            return Default;
        if (variant.Word3 != Schema)
            throw new InvalidOperationException("Unknown DDGI resource descriptor schema.");
        var descriptor = new DDGIResourceDescriptor(new IVector3((int)(uint)variant.Word0, (int)(variant.Word0 >> 32), (int)(uint)variant.Word1),
            (int)(variant.Word1 >> 32), (int)(uint)variant.Word2, (int)(variant.Word2 >> 32));
        descriptor.Validate();
        return descriptor;
    }

    private static ulong Pack(uint low, uint high) => low | ((ulong)high << 32);

    private void Validate()
    {
        if (Counts.X < 1 || Counts.Y < 1 || Counts.Z < 1 || RaysPerProbe < 1 || Cascades is < 1 or > 4 || MaxUpdatedProbes < 0)
            throw new InvalidOperationException("DDGI resource counts, rays and cascades must be positive and valid.");
        // These are explicit implementation bounds, not silently reduced budgets.
        // The current kernels dispatch one workgroup per probe and 32 rays per group.
        if (ScheduledCapacity > 65535 || RayElements > 65535u * 32u)
            throw new InvalidOperationException("DDGI exceeds the compute dispatch range. Reduce the probe update budget or rays per probe.");
        if (VisibilityWidth > 16384 || VisibilityHeight > 16384)
            throw new InvalidOperationException("DDGI atlas dimensions exceed the supported 16384-texel implementation limit.");
        ulong bytes = (ulong)ProbeElements * 32UL + (ulong)RayElements * 96UL +
            (ulong)Cascades * 4UL * ((ulong)IrradianceWidth * IrradianceHeight + (ulong)VisibilityWidth * VisibilityHeight);
        if (bytes > 512UL * 1024UL * 1024UL)
            throw new InvalidOperationException("DDGI volume allocations exceed the 512 MiB implementation limit. Reduce grid dimensions, cascades or update budget.");
    }
}
