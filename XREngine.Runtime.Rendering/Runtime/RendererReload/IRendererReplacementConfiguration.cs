namespace XREngine.Rendering;

/// <summary>
/// Changes external renderer configuration only while all affected windows are detached.
/// The caller captures the previous values before starting the replacement.
/// </summary>
public interface IRendererReplacementConfiguration
{
    /// <summary>Applies the candidate configuration after every old renderer is destroyed.</summary>
    void ApplyAfterDetach();

    /// <summary>Restores the captured configuration after candidate teardown and before rollback attachment.</summary>
    void RestoreBeforeRollback();
}
