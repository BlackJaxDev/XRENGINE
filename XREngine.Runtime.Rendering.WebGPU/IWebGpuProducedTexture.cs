namespace XREngine.Rendering.WebGPU;

/// <summary>Tracks framebuffer writes separately from sampled reads until the owning frame commits.</summary>
internal interface IWebGpuProducedTexture
{
    void MarkProduced();
    void CommitProducedFrame(uint frameSequence);
    bool WasProducedInFrame(uint frameSequence);
    bool HasCommittedProduction { get; }
}
