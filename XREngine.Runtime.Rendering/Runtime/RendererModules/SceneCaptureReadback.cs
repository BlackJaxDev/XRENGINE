namespace XREngine.Rendering;

/// <summary>CPU pixels with the exact accepted producer and physical texture-generation receipt.</summary>
public sealed class SceneCaptureReadback
{
    internal SceneCaptureReadback(float[] rgba, int width, int height, int arrayLayer,
        long backendGeneration, int textureGenerationHandle, uint acceptedFrameSequence)
    {
        Rgba = rgba; Width = width; Height = height; ArrayLayer = arrayLayer;
        BackendGeneration = backendGeneration; TextureGenerationHandle = textureGenerationHandle;
        AcceptedFrameSequence = acceptedFrameSequence;
    }

    public float[] Rgba { get; }
    public int Width { get; }
    public int Height { get; }
    public int ArrayLayer { get; }
    public long BackendGeneration { get; }
    public int TextureGenerationHandle { get; }
    public uint AcceptedFrameSequence { get; }
    internal object? ProducerOwner { get; init; }
    internal XRTexture2DArray? Source { get; init; }
    internal int Session { get; init; }
    internal ulong LayerProductionTicket { get; init; }
    internal byte[] PersistedContentHash { get; init; } = [];

    internal SceneCaptureReadback WithoutPixels(bool copyHash = false)
        => new([], Width, Height, ArrayLayer, BackendGeneration, TextureGenerationHandle, AcceptedFrameSequence)
        {
            ProducerOwner = ProducerOwner, Source = Source, Session = Session,
            LayerProductionTicket = LayerProductionTicket,
            PersistedContentHash = copyHash ? [.. PersistedContentHash] : PersistedContentHash,
        };
}
