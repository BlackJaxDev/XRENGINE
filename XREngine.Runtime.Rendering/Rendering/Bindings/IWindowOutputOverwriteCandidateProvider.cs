using XREngine.Rendering.UI;

namespace XREngine.Rendering;

/// <summary>Offers a presentation draw for a checked native overwrite.</summary>
public interface IWindowOutputOverwriteCandidateProvider
{
    bool TryGetWindowOutputOverwriteCandidate(out UIBackdropOutputOverwriteCandidate candidate);
}
