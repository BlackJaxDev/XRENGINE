namespace XREngine.Rendering.Commands;

/// <summary>
/// Steps of one Vulkan Advanced visibility family preparation that the S13a
/// telemetry times and allocation-counts separately. Indices address the
/// per-step counter arrays in the telemetry snapshot.
/// </summary>
public enum S13aAdvancedFamilyStep
{
    /// <summary>Native target snapshot, prewarm target and format validation for raster and late-raster stages.</summary>
    TargetClosure = 0,
    /// <summary>Canonical scene publication preparation (once per compatible family).</summary>
    ScenePublication = 1,
    /// <summary>Stable-bin geometry stream, manifest resolution and submission-plan sealing for the raster stage.</summary>
    BinSealing = 2,
    /// <summary>Resident template retention for the sealed bins.</summary>
    TemplateRetention = 3,
    /// <summary>Visibility raster pipeline preparation against the target closure.</summary>
    RasterPipelines = 4,
    /// <summary>Per-stage target, MSAA resolve and publication association into the sealed stream.</summary>
    StageAssociation = 5,
    /// <summary>Raster payload build and set-1 frame-slot realization of the family resources.</summary>
    RasterRealization = 6,
    /// <summary>Early visibility and build-indirect compute pipeline lookup.</summary>
    ComputePipelines = 7,
    /// <summary>Per-stage set-1 state association into the sealed stream.</summary>
    StateAssociation = 8,
    /// <summary>Native compute closure capture, descriptors, pipeline lookup and association.</summary>
    NativeCompute = 9,
    /// <summary>Late target closure, late pipelines, association and depth-pyramid descriptor sealing.</summary>
    LateClosure = 10,
    /// <summary>Bin sealing sub-step: visibility geometry stream construction and freeze ordering.</summary>
    BinGeometryStream = 11,
    /// <summary>Bin sealing sub-step: header grouping and manifest resolution.</summary>
    BinManifests = 12,
    /// <summary>Bin sealing sub-step: submission-plan sealing against the indirect ranges.</summary>
    BinSubmissionPlans = 13,
    /// <summary>One pipeline runtime readiness check (<c>GetReadiness</c>) including its request path.</summary>
    ReadinessCall = 14,
    /// <summary>Readiness sub-step: waiting for the preparation gate.</summary>
    ReadinessLockWait = 15,
    /// <summary>Readiness sub-step: generated shader source refresh against asset revisions.</summary>
    ReadinessSourceRefresh = 16,
    /// <summary>Readiness sub-step: shader source identity hash over every required program.</summary>
    ReadinessIdentity = 17,
    /// <summary>Readiness sub-step: program link-configuration and native-handle currentness checks.</summary>
    ReadinessCurrentness = 18,
    /// <summary>Raster pipelines sub-step: readiness plus raster program wrapper lookup per bin header.</summary>
    RasterProgramLookup = 19,
    /// <summary>Raster pipelines sub-step: graphics pipeline key construction and shared pipeline lookup per bin header.</summary>
    RasterPipelineFactory = 20,
    /// <summary>Raster pipelines sub-step: per-record geometry validation and header native state per bin header.</summary>
    RasterHeaderValidation = 21,
    /// <summary>Native compute sub-step: closure reuse or capture.</summary>
    NativeClosureCapture = 22,
    /// <summary>Native compute sub-step: descriptor set preparation.</summary>
    NativeDescriptors = 23,
    /// <summary>Native compute sub-step: readiness plus prepared pipeline lookup.</summary>
    NativePipelineLookup = 24,
    /// <summary>Native compute sub-step: shading address root and closure association.</summary>
    NativeAssociation = 25,
    /// <summary>Geometry stream sub-step: per-payload draw and geometry resolution, closure validation and record append.</summary>
    BinGeometryPayloads = 26,
    /// <summary>Geometry stream sub-step: freeze ordering of the appended records.</summary>
    BinGeometryFreeze = 27,
}
