namespace XREngine.Rendering.WebGPU;

/// <summary>Managed ownership state; request identities are never GPU resource handles.</summary>
internal enum WebGpuResourceRequestState { Queued, Submitted, Ready, Failed, Cancelled }
