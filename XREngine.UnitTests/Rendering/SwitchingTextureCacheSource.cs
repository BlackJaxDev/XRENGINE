using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>Changes selected file between the cache's metadata checks.</summary>
internal sealed class SwitchingTextureCacheSource(string firstPath, string secondPath) : ITextureStreamingSource
{
    public int ResolveCalls { get; private set; }
    public string SourcePath => firstPath;

    public string ResolveCacheSourcePath() => ++ResolveCalls == 1 ? firstPath : secondPath;

    public TextureStreamingResidentData LoadResidentData(uint maxResidentDimension, bool includeMipChain, CancellationToken token)
        => throw new NotSupportedException();
}
