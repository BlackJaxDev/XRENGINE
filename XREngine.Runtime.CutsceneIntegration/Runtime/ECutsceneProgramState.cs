namespace XREngine.Cutscenes;

/// <summary>The state of the shader programs of one material during a cutscene open.</summary>
public enum ECutsceneProgramState
{
    /// <summary>The programs are not ready yet. The open waits for them.</summary>
    Pending,

    /// <summary>The programs are ready for a draw.</summary>
    Ready,

    /// <summary>The programs failed. The open reports a diagnostic and does not hide the failure.</summary>
    Failed,
}
