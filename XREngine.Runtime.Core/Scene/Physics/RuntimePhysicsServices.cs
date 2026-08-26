using System.Numerics;
using XREngine.Components;
using XREngine.Components.Physics;
using XREngine.Data.Colors;

namespace XREngine.Scene.Physics;

/// <summary>
/// Supplies host timing and transient debug drawing required by physics backends.
/// </summary>
public interface IRuntimePhysicsServices
{
    float FixedDeltaSeconds { get; }
    bool IsPhysicsThread => true;
    bool IsShuttingDown => false;
    long ElapsedTicks => 0L;
    PhysicsVisualizeSettings VisualizeSettings { get; }
    bool JoltDebugRenderDiagnostics { get; }

    void RenderPoint(Vector3 position, ColorF4 color);
    void RenderLine(Vector3 start, Vector3 end, ColorF4 color);
    void RenderSphere(Vector3 center, float radius, bool solid, ColorF4 color);
    void RenderCapsule(Vector3 start, Vector3 end, float radius, bool solid, ColorF4 color);
}

/// <summary>
/// Supplies renderer/model-owner collision triangles to Core convex cooking without
/// coupling physics simulation to authored mesh component implementations.
/// </summary>
public interface IConvexHullInputProvider
{
    bool TryCollect(XRComponent component, out ConvexHullInputCollection inputs, out string targetLabel);
}

/// <summary>
/// Process-wide physics host services. Application composition replaces the no-op default.
/// </summary>
public static class RuntimePhysicsServices
{
    private static readonly IRuntimePhysicsServices Default = new DefaultRuntimePhysicsServices();
    private static IRuntimePhysicsServices _current = Default;
    private static IConvexHullInputProvider? _convexHullInputs;

    public static IRuntimePhysicsServices Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>
    /// Optional higher-level mesh extraction adapter. Hosts install this for the lifetime
    /// of their model/render composition; cooking remains available from explicitly
    /// supplied collision inputs when no adapter is present.
    /// </summary>
    public static IConvexHullInputProvider? ConvexHullInputs
    {
        get => Volatile.Read(ref _convexHullInputs);
        set => Volatile.Write(ref _convexHullInputs, value);
    }

    /// <summary>
    /// Installs host-facing physics services and restores the prior composition when disposed.
    /// </summary>
    public static IDisposable Install(
        IRuntimePhysicsServices services,
        IConvexHullInputProvider? convexHullInputs = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        IRuntimePhysicsServices previousServices = Interlocked.Exchange(ref _current, services);
        IConvexHullInputProvider? previousInputs = Interlocked.Exchange(ref _convexHullInputs, convexHullInputs);
        return new InstallationLease(services, convexHullInputs, previousServices, previousInputs);
    }

    private sealed class InstallationLease(
        IRuntimePhysicsServices installedServices,
        IConvexHullInputProvider? installedInputs,
        IRuntimePhysicsServices previousServices,
        IConvexHullInputProvider? previousInputs) : IDisposable
    {
        private IRuntimePhysicsServices? _installedServices = installedServices;

        public void Dispose()
        {
            IRuntimePhysicsServices? current = Interlocked.Exchange(ref _installedServices, null);
            if (current is null)
                return;

            Interlocked.CompareExchange(ref _current, previousServices, current);
            Interlocked.CompareExchange(ref _convexHullInputs, previousInputs, installedInputs);
        }
    }

    private sealed class DefaultRuntimePhysicsServices : IRuntimePhysicsServices
    {
        private readonly PhysicsVisualizeSettings _visualizeSettings = new();

        public float FixedDeltaSeconds => 1.0f / 60.0f;
        public PhysicsVisualizeSettings VisualizeSettings => _visualizeSettings;
        public bool JoltDebugRenderDiagnostics => false;

        public void RenderPoint(Vector3 position, ColorF4 color) { }
        public void RenderLine(Vector3 start, Vector3 end, ColorF4 color) { }
        public void RenderSphere(Vector3 center, float radius, bool solid, ColorF4 color) { }
        public void RenderCapsule(Vector3 start, Vector3 end, float radius, bool solid, ColorF4 color) { }
    }
}
