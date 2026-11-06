namespace XREngine.Cutscenes;

/// <summary>The state of the residency manifest of a cutscene asset.</summary>
public enum ECutsceneManifestState
{
    /// <summary>The asset has no manifest. Playback uses the approximate manifest.</summary>
    Missing,

    /// <summary>The asset changed after the bake. Playback uses the approximate manifest.</summary>
    Stale,

    /// <summary>The manifest belongs to the current content of the asset.</summary>
    Current,

    /// <summary>The manifest data is not valid, or the content hash could not be computed. Playback uses the approximate manifest.</summary>
    Invalid,

    /// <summary>
    /// The content hash is current, but too many baked textures are not loaded textures of the scene, and no import is
    /// active. The asset keys of the scene changed after the bake, for example because the assets moved or are outside the
    /// game and engine asset folders. Playback uses the approximate manifest.
    /// </summary>
    Unmatched,
}
