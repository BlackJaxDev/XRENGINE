using XREngine;
using XREngine.Runtime.Bootstrap;

namespace RollingBall;

/// <summary>Provides optional platform composition without adding platform dependencies to gameplay.</summary>
public interface IRollingBallPlatformHost
{
    RuntimeApplicationProfile ApplicationProfile { get; }

    /// <summary>Creates startup settings and may augment the loaded world before it enters play.</summary>
    GameStartupSettings CreateStartupSettings(RollingBallWorldAsset world, bool runtimeSmoke);

    /// <summary>Opens an optional host-owned diagnostics log outside the portable game assembly.</summary>
    string OpenDiagnostics(string requestedPath, string initialLine);

    /// <summary>Appends an optional host-owned diagnostics record.</summary>
    void AppendDiagnostics(string path, string line);
}
