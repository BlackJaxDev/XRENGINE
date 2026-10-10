namespace XREngine.Rendering.WebGPU;

/// <summary>One exact source-to-output vertex relation; all offsets are bytes within a canonical geometry stream.</summary>
internal readonly record struct WebGpuAdvancedNativeVertexJob(
    int PayloadIndex,
    uint DrawDense,
    AdvancedVisibilityPayload Source,
    AdvancedNativeVertexMaterial Material,
    AdvancedNativeVertexInputs PreviousInputs,
    uint CurrentOutputOffset,
    uint PreviousOutputOffset,
    bool PreviousValid);
