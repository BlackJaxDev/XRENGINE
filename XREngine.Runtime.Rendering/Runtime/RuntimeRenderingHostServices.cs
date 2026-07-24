using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Shadows;
using XREngine.Scene;
namespace XREngine.Rendering;

/// <summary>
/// Static access point for the host service implementation used by runtime rendering code.
/// </summary>
public static class RuntimeRenderingHostServices
{
    private static readonly UninstalledRuntimeRenderingHostServices Uninstalled = new();
    private static IRuntimeRenderingHostServices _current = Uninstalled;
    private static IRuntimeRenderSettingsServices _settings = Uninstalled;
    private static IRuntimeRenderFrameTimingServices _frameTiming = Uninstalled;
    private static IRuntimeRenderProfilingServices _profiling = Uninstalled;
    private static IRuntimeRenderDiagnosticsServices _diagnostics = Uninstalled;
    private static IRuntimeRenderStatisticsServices _statistics = Uninstalled;
    private static IRuntimeRenderDebugDrawingServices _debugDrawing = Uninstalled;
    private static IRuntimeRenderSchedulingServices? _scheduling;
    private static IRuntimeRenderAssetServices? _assets;
    private static IRuntimeRendererFactoryServices? _factories;
    private static IRuntimeRenderPresentationServices? _presentation;
    private static IRuntimeRenderBackendInteropServices? _backendInterop;

    /// <summary>
    /// Current composite host facade. Assigning <see langword="null"/> tears down the installation.
    /// Required focused capabilities fail fast while no host is installed.
    /// </summary>
    public static IRuntimeRenderingHostServices Current
    {
        get => _current;
        set
        {
            IRuntimeRenderingHostServices previous = _current;
            IRuntimeRenderingHostServices current = value ?? Uninstalled;
            if (ReferenceEquals(previous, current))
                return;

            _current = current;
            _settings = current;
            _frameTiming = current;
            _profiling = current is UninstalledRuntimeRenderingHostServices ? Uninstalled : current;
            _diagnostics = current is UninstalledRuntimeRenderingHostServices ? Uninstalled : current;
            _statistics = current is UninstalledRuntimeRenderingHostServices ? Uninstalled : current;
            _debugDrawing = current is UninstalledRuntimeRenderingHostServices ? Uninstalled : current;
            bool installed = current is not UninstalledRuntimeRenderingHostServices;
            _scheduling = installed ? current : null;
            _assets = installed ? current : null;
            _factories = installed ? current : null;
            _presentation = installed ? current : null;
            _backendInterop = installed ? current : null;
            RuntimeEngine.Time.Timer.RebindHost(previous, current);
            RuntimeEngine.Rendering.RebindSettingsChangedHandlers(previous, current);
        }
    }

    /// <summary>
    /// Cold render configuration. Defaults remain available before host installation.
    /// </summary>
    public static IRuntimeRenderSettingsServices Settings => _settings;

    /// <summary>
    /// Allocation-free render timing and frame state.
    /// </summary>
    public static IRuntimeRenderFrameTimingServices FrameTiming => _frameTiming;

    /// <summary>
    /// Optional allocation-free profiling sink.
    /// </summary>
    public static IRuntimeRenderProfilingServices Profiling => _profiling;

    /// <summary>
    /// Optional diagnostics and logging sink.
    /// </summary>
    public static IRuntimeRenderDiagnosticsServices Diagnostics => _diagnostics;

    /// <summary>
    /// Optional allocation-free statistics sink.
    /// </summary>
    public static IRuntimeRenderStatisticsServices Statistics => _statistics;

    /// <summary>
    /// Optional allocation-free debug drawing sink.
    /// </summary>
    public static IRuntimeRenderDebugDrawingServices DebugDrawing => _debugDrawing;

    /// <summary>
    /// Required frame and thread scheduler.
    /// </summary>
    public static IRuntimeRenderSchedulingServices Scheduling
        => _scheduling ?? ThrowMissingCapability<IRuntimeRenderSchedulingServices>("render-thread scheduling");

    /// <summary>
    /// Required asset and texture IO.
    /// </summary>
    public static IRuntimeRenderAssetServices Assets
        => _assets ?? ThrowMissingCapability<IRuntimeRenderAssetServices>("asset and texture IO");

    /// <summary>
    /// Required renderer, window, panel, and render-pipeline factories.
    /// </summary>
    public static IRuntimeRendererFactoryServices Factories
        => _factories ?? ThrowMissingCapability<IRuntimeRendererFactoryServices>("renderer/window/panel factories");

    /// <summary>
    /// Required desktop and XR presentation services.
    /// </summary>
    public static IRuntimeRenderPresentationServices Presentation
        => _presentation ?? ThrowMissingCapability<IRuntimeRenderPresentationServices>("desktop/VR/OpenXR presentation");

    /// <summary>
    /// Required renderer backend interop services.
    /// </summary>
    public static IRuntimeRenderBackendInteropServices BackendInterop
        => _backendInterop ?? ThrowMissingCapability<IRuntimeRenderBackendInteropServices>("renderer backend interop");

    internal static bool HasConcreteHost => _current is not UninstalledRuntimeRenderingHostServices;

    /// <summary>
    /// Installs a host and returns a scope that restores the previous installation.
    /// Disposing an older scope never tears down a newer replacement.
    /// </summary>
    public static IDisposable Install(IRuntimeRenderingHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        IRuntimeRenderingHostServices previous = _current;
        Current = host;
        return new RuntimeRenderingHostInstallationScope(host, previous);
    }

    /// <summary>
    /// Removes the current host installation.
    /// </summary>
    public static void Reset()
        => Current = Uninstalled;

    private static TCapability ThrowMissingCapability<TCapability>(string capabilityName)
        where TCapability : class
    {
        throw new InvalidOperationException(
            $"Runtime rendering requires {capabilityName}, but no host is installed. " +
            $"Install an {nameof(IRuntimeRenderingHostServices)} at the application composition root before starting rendering.");
    }

    /// <summary>
    /// Absolute path to the game-level cache directory.  Set by the host engine
    /// during initialization.  Used by <c>BvhDiskCache</c> and similar caches
    /// that live in the rendering layer.
    /// </summary>
    public static string? GameCachePath { get; set; }

    /// <summary>
    /// Host-registered callback used by OpenXR recovery when a process-scoped runtime service
    /// such as Monado may need to be relaunched before probing the loader again.
    /// </summary>
    public static Func<string, bool>? OpenXrRuntimeServiceEnsurer { get; set; }

    public static bool TryEnsureOpenXrRuntimeService(string reason)
    {
        Func<string, bool>? ensurer = OpenXrRuntimeServiceEnsurer;
        return ensurer is not null && ensurer(reason);
    }


}
