namespace XREngine.Rendering;

/// <summary>
/// The open measurement of one OpenGL apply or sparse transition: a CPU timer without GC pauses and a GPU timestamp pair.
/// </summary>
/// <param name="Timer">The CPU timer of the apply.</param>
/// <param name="GpuPair">The pair of <see cref="TextureStreamingCapacity.OpenGlGpuTimer"/>, or -1 when the GPU is not timed.</param>
internal readonly record struct TextureStreamingApplyMeasurement(TextureStreamingWorkTimer Timer, int GpuPair);
