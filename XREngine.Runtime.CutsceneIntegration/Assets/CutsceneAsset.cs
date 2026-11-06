using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine.Cutscenes;

/// <summary>A timeline asset. It holds the tracks, the clips on them, and the bindings that the tracks act on.</summary>
public class CutsceneAsset : XRAsset
{
    private CutsceneTime _duration;
    private bool _forceRestoreAll;
    private CutsceneResidencyManifest? _manifest;
    private ulong _manifestContentHash;
    private List<AudioProxyAsset> _audioProxies = [];

    /// <summary>The length of the cutscene.</summary>
    public CutsceneTime Duration
    {
        get => _duration;
        set => SetField(ref _duration, value);
    }

    /// <summary>True to restore every target when the cutscene ends, whatever the track setting is.</summary>
    public bool ForceRestoreAll
    {
        get => _forceRestoreAll;
        set => SetField(ref _forceRestoreAll, value);
    }

    /// <summary>The tracks of the cutscene.</summary>
    public List<CutsceneTrack> Tracks { get; set; } = [];

    /// <summary>The targets that the tracks refer to by index.</summary>
    public List<CutsceneBindingReference> Bindings { get; set; } = [];

    /// <summary>
    /// The residency manifest that the bake made, or null. Playback uses it only while <see cref="ManifestContentHash"/>
    /// matches <see cref="CutsceneManifestHash.Compute"/>. Otherwise it uses the approximate manifest and adds a diagnostic.
    /// </summary>
    public CutsceneResidencyManifest? Manifest
    {
        get => _manifest;
        set => SetField(ref _manifest, value);
    }

    /// <summary>The content hash of the asset at the time of the bake. See <see cref="CutsceneManifestHash"/>.</summary>
    public ulong ManifestContentHash
    {
        get => _manifestContentHash;
        set => SetField(ref _manifestContentHash, value);
    }

    /// <summary>
    /// The audio proxies that the bake made: one small mono IMA ADPCM copy of each audio clip source file. Each proxy
    /// refers to its source with <see cref="AudioProxyAsset.SourceReference"/>. The manifest lists each proxy as an
    /// <see cref="EResidencyAssetKind.Audio"/> asset with the key <see cref="CutsceneResidencyAssetKeys.ForAudioSource"/>.
    /// The proxies are not part of <see cref="CutsceneManifestHash"/>.
    /// </summary>
    public List<AudioProxyAsset> AudioProxies
    {
        get => _audioProxies;
        set => SetField(ref _audioProxies, value ?? []);
    }
}
