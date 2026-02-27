using System.Diagnostics;

namespace XREngine.Audio.Steam;

/// <summary>
/// Provides offline baking workflows for Steam Audio reflections and pathing data.
/// <para>
/// Baking precomputes simulation results at each probe in a <see cref="SteamAudioProbeBatch"/>
/// and stores them in the batch's associated data layers. The baked data can then be used at
/// runtime to accelerate reflection and pathing simulation (avoiding expensive real-time
/// ray tracing for known static geometry).
/// </para>
/// <para>
/// Baking is a long-running, potentially multi-threaded operation. Use the progress callback
/// to report progress to UI. Baking can be cancelled via <see cref="CancelReflectionsBake"/>
/// or <see cref="CancelPathBake"/>.
/// </para>
/// </summary>
public sealed class SteamAudioBaker
{
    private readonly IPLContext _context;

    internal SteamAudioBaker(IPLContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Bakes reflection data (convolution IRs and/or parametric reverb) into a probe batch.
    /// This is a blocking call that may take seconds to minutes depending on scene complexity.
    /// </summary>
    /// <param name="scene">The committed scene containing geometry.</param>
    /// <param name="probeBatch">The committed probe batch to bake into.</param>
    /// <param name="settings">Bake quality settings.</param>
    /// <param name="progress">Optional progress callback (0.0–1.0).</param>
    public void BakeReflections(
        SteamAudioScene scene,
        SteamAudioProbeBatch probeBatch,
        ReflectionsBakeSettings settings,
        Action<float>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(probeBatch);

        if (!scene.IsCommitted)
            throw new InvalidOperationException("Scene must be committed before baking reflections.");
        if (!probeBatch.IsCommitted)
            throw new InvalidOperationException("Probe batch must be committed before baking.");

        var identifier = new IPLBakedDataIdentifier
        {
            type = IPLBakedDataType.IPL_BAKEDDATATYPE_REFLECTIONS,
            variation = settings.Variation,
            endpointInfluence = settings.EndpointInfluence,
        };

        IPLReflectionsBakeFlags bakeFlags = 0;
        if (settings.BakeConvolution)
            bakeFlags |= IPLReflectionsBakeFlags.IPL_REFLECTIONSBAKEFLAGS_BAKECONVOLUTION;
        if (settings.BakeParametric)
            bakeFlags |= IPLReflectionsBakeFlags.IPL_REFLECTIONSBAKEFLAGS_BAKEPARAMETRIC;

        var bakeParams = new IPLReflectionsBakeParams
        {
            scene = scene.Handle,
            probeBatch = probeBatch.Handle,
            sceneType = IPLSceneType.IPL_SCENETYPE_DEFAULT,
            identifier = identifier,
            bakeFlags = bakeFlags,
            numRays = settings.NumRays,
            numDiffuseSamples = settings.NumDiffuseSamples,
            numBounces = settings.NumBounces,
            simulatedDuration = settings.SimulatedDuration,
            savedDuration = settings.SavedDuration,
            order = settings.AmbisonicsOrder,
            numThreads = settings.NumThreads,
            rayBatchSize = settings.RayBatchSize,
            irradianceMinDistance = settings.IrradianceMinDistance,
            bakeBatchSize = 1,
        };

        IPLProgressCallback? nativeCallback = null;
        if (progress is not null)
            nativeCallback = (p, _) => progress(p);

        Debug.WriteLine($"[SteamAudioBaker] Baking reflections: {probeBatch.ProbeCount} probes, " +
                        $"{settings.NumRays} rays, {settings.NumBounces} bounces, {settings.SimulatedDuration}s duration...");

        Phonon.iplReflectionsBakerBake(_context, ref bakeParams, nativeCallback, IntPtr.Zero);

        Debug.WriteLine("[SteamAudioBaker] Reflections bake complete.");
    }

    /// <summary>
    /// Cancels an in-progress reflections bake.
    /// </summary>
    public void CancelReflectionsBake()
    {
        Phonon.iplReflectionsBakerCancelBake(_context);
    }

    /// <summary>
    /// Bakes pathing data (probe-to-probe visibility and shortest paths) into a probe batch.
    /// This is a blocking call.
    /// </summary>
    /// <param name="scene">The committed scene containing geometry.</param>
    /// <param name="probeBatch">The committed probe batch to bake into.</param>
    /// <param name="settings">Bake quality settings.</param>
    /// <param name="progress">Optional progress callback (0.0–1.0).</param>
    public void BakePathing(
        SteamAudioScene scene,
        SteamAudioProbeBatch probeBatch,
        PathBakeSettings settings,
        Action<float>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(probeBatch);

        if (!scene.IsCommitted)
            throw new InvalidOperationException("Scene must be committed before baking pathing.");
        if (!probeBatch.IsCommitted)
            throw new InvalidOperationException("Probe batch must be committed before baking.");

        var identifier = new IPLBakedDataIdentifier
        {
            type = IPLBakedDataType.IPL_BAKEDDATATYPE_PATHING,
            variation = IPLBakedDataVariation.IPL_BAKEDDATAVARIATION_DYNAMIC,
        };

        var bakeParams = new IPLPathBakeParams
        {
            scene = scene.Handle,
            probeBatch = probeBatch.Handle,
            identifier = identifier,
            numSamples = settings.NumSamples,
            radius = settings.VisRadius,
            threshold = settings.VisThreshold,
            visRange = settings.VisRange,
            pathRange = settings.PathRange,
            numThreads = settings.NumThreads,
        };

        IPLProgressCallback? nativeCallback = null;
        if (progress is not null)
            nativeCallback = (p, _) => progress(p);

        Debug.WriteLine($"[SteamAudioBaker] Baking pathing: {probeBatch.ProbeCount} probes, " +
                        $"{settings.NumSamples} samples, visRange={settings.VisRange}m...");

        Phonon.iplPathBakerBake(_context, ref bakeParams, nativeCallback, IntPtr.Zero);

        Debug.WriteLine("[SteamAudioBaker] Pathing bake complete.");
    }

    /// <summary>
    /// Cancels an in-progress pathing bake.
    /// </summary>
    public void CancelPathBake()
    {
        Phonon.iplPathBakerCancelBake(_context);
    }
}

/// <summary>
/// Settings for baking reflection data into a probe batch.
/// </summary>
public sealed class ReflectionsBakeSettings
{
    /// <summary>Number of rays per probe for baking. More = better quality, slower. Default: 16384.</summary>
    public int NumRays { get; set; } = 16384;

    /// <summary>Number of diffuse sample directions. Default: 1024.</summary>
    public int NumDiffuseSamples { get; set; } = 1024;

    /// <summary>Number of ray bounces. Default: 16.</summary>
    public int NumBounces { get; set; } = 16;

    /// <summary>Simulated IR duration in seconds (used for parametric estimation). Default: 2.0.</summary>
    public float SimulatedDuration { get; set; } = 2.0f;

    /// <summary>Saved IR duration in seconds (stored convolution data). Default: 1.0.</summary>
    public float SavedDuration { get; set; } = 1.0f;

    /// <summary>Ambisonics order for baked IRs. Default: 1 (first-order, 4 channels).</summary>
    public int AmbisonicsOrder { get; set; } = 1;

    /// <summary>Number of bake threads. Default: Environment.ProcessorCount.</summary>
    public int NumThreads { get; set; } = Environment.ProcessorCount;

    /// <summary>Ray batch size for the ray tracer. Default: 64.</summary>
    public int RayBatchSize { get; set; } = 64;

    /// <summary>Minimum irradiance distance. Default: 1.0.</summary>
    public float IrradianceMinDistance { get; set; } = 1.0f;

    /// <summary>Whether to bake convolution IRs. Default: true.</summary>
    public bool BakeConvolution { get; set; } = true;

    /// <summary>Whether to bake parametric reverb data. Default: true.</summary>
    public bool BakeParametric { get; set; } = true;

    /// <summary>Variation type for the baked data layer. Default: Reverb (environment-wide).</summary>
    public IPLBakedDataVariation Variation { get; set; } = IPLBakedDataVariation.IPL_BAKEDDATAVARIATION_REVERB;

    /// <summary>Source/listener endpoint influence sphere. Only used for StaticSource/StaticListener variations.</summary>
    public IPLSphere EndpointInfluence { get; set; }
}

/// <summary>
/// Settings for baking pathing data into a probe batch.
/// </summary>
public sealed class PathBakeSettings
{
    /// <summary>Number of point samples per probe for visibility testing. Default: 16.</summary>
    public int NumSamples { get; set; } = 16;

    /// <summary>Probe sphere radius for visibility testing (meters). Default: 1.0.</summary>
    public float VisRadius { get; set; } = 1.0f;

    /// <summary>Fraction of unoccluded rays needed for mutual visibility. Default: 0.1.</summary>
    public float VisThreshold { get; set; } = 0.1f;

    /// <summary>Maximum probe-to-probe distance for visibility testing (meters). Default: 50.0.</summary>
    public float VisRange { get; set; } = 50.0f;

    /// <summary>Maximum path length between probes (meters). Default: 200.0.</summary>
    public float PathRange { get; set; } = 200.0f;

    /// <summary>Number of threads. Default: Environment.ProcessorCount.</summary>
    public int NumThreads { get; set; } = Environment.ProcessorCount;
}
