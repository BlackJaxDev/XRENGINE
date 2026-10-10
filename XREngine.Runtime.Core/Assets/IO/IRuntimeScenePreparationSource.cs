using XREngine.Scene;

namespace XREngine.Core.Files;

/// <summary>Prepares a catalog-loaded scene's declared runtime resources before attachment.</summary>
public interface IRuntimeScenePreparationSource
{
    Task PrepareSceneAsync(XRScene scene, string catalogPath, CancellationToken cancellationToken = default);
}
