namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Configuration for creating a MotionBricks inference session.
/// </summary>
public sealed class MotionBricksSessionConfig
{
    /// <summary>
    /// Full path to the staged SOMA ONNX file (for example <c>mmm_v6_soma_fp16.onnx</c>).
    /// </summary>
    public string OnnxModelPath { get; init; } = string.Empty;

    /// <summary>
    /// Optional directory where compiled TensorRT <c>.plan</c> files are cached across launches.
    /// </summary>
    public string PlanCacheDirectory { get; init; } = string.Empty;

    /// <summary>
    /// Enables FP16 kernel execution in TensorRT while keeping FP32 I/O tensors.
    /// </summary>
    public bool EnableFp16 { get; init; } = true;

    /// <summary>
    /// Enables TF32 execution on Ampere and newer NVIDIA GPUs.
    /// </summary>
    public bool EnableTf32 { get; init; } = true;

    /// <summary>
    /// TensorRT builder optimization level from 0 (fastest build) to 5 (maximum tuning).
    /// </summary>
    public int BuilderOptimizationLevel { get; init; } = 3;

    /// <summary>
    /// Forces a fresh TensorRT plan build even when a matching cached plan exists.
    /// </summary>
    public bool ForceRebuild { get; init; }

    /// <summary>
    /// Optional TensorRT builder workspace limit in bytes. Zero keeps TensorRT's default limit.
    /// </summary>
    public long WorkspaceSizeBytes { get; init; }
}
