using XREngine.ControlPlane;

namespace XREngine.Editor;

/// <summary>Verified native input retained only for the current staged browser publication.</summary>
internal sealed record BrowserSharedWorldPackage(WorldPackageManifest Manifest, byte[] NativeWorldBytes, string BrowserWorldPath);
