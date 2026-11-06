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
    private ECutsceneVrCameraMode _vrCameraMode = ECutsceneVrCameraMode.SpectatorOnly;
    private float _rigCutFadeSeconds = DefaultRigCutFadeSeconds;

    /// <summary>The default of <see cref="RigCutFadeSeconds"/>, in seconds.</summary>
    public const float DefaultRigCutFadeSeconds = 0.5f;

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

    /// <summary>
    /// How the cutscene shows its shots to a VR user. The default is <see cref="ECutsceneVrCameraMode.SpectatorOnly"/>.
    /// The mode applies only while a VR session runs when the cutscene opens.
    /// </summary>
    public ECutsceneVrCameraMode VrCameraMode
    {
        get => _vrCameraMode;
        set => SetField(ref _vrCameraMode, value);
    }

    /// <summary>
    /// The full time in seconds of one headset fade through black in <see cref="ECutsceneVrCameraMode.RigAnchor"/> mode.
    /// Half of it fades to black, and the other half fades back in. It must be finite and greater than zero, so a cut never
    /// moves the rig while the user can see. The default is <see cref="DefaultRigCutFadeSeconds"/>.
    /// </summary>
    public float RigCutFadeSeconds
    {
        get => _rigCutFadeSeconds;
        set
        {
            if (!float.IsFinite(value) || value <= 0.0f)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The rig cut fade time must be finite and greater than zero.");

            SetField(ref _rigCutFadeSeconds, value);
        }
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
