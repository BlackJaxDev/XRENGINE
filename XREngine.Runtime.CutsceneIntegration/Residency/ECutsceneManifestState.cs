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
}
