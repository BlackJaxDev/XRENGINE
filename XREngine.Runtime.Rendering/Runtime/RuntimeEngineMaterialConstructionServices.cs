namespace XREngine.Rendering;

/// <summary>
/// Application-owned target selection for built-in material construction. Browser composition
/// must install its cooked target before deserializing materials; desktop retains its GLSL path.
/// </summary>
public static class RuntimeEngineMaterialConstructionServices
{
    private static Installation? _current;

    /// <summary>
    /// Gets the explicitly installed target, or the legacy desktop GLSL target on desktop.
    /// Browser construction without a target installation fails before shader asset IO.
    /// </summary>
    public static EngineMaterialConstructionTarget Target
        => Volatile.Read(ref _current)?.Target ?? (OperatingSystem.IsBrowser()
            ? throw new InvalidOperationException(
                "WebGPU.MaterialConstruction.TargetRequired: install the browser material target before asset deserialization.")
            : EngineMaterialConstructionTarget.DesktopGlsl);

    /// <summary>Installs a material target until the returned lease is disposed.</summary>
    public static IDisposable Install(EngineMaterialConstructionTarget target)
    {
        if (!Enum.IsDefined(target))
            throw new ArgumentOutOfRangeException(nameof(target));
        if (OperatingSystem.IsBrowser() && target != EngineMaterialConstructionTarget.WebGpuCooked)
            throw new InvalidOperationException(
                "WebGPU.MaterialConstruction.TargetMismatch: browser materials require the cooked WebGPU target.");

        Installation installation = new(target);
        if (Interlocked.CompareExchange(ref _current, installation, null) is not null)
            throw new InvalidOperationException(
                "MaterialConstruction.TargetConflict: dispose the active installation before replacing its target.");
        return installation;
    }

    private sealed class Installation(EngineMaterialConstructionTarget target) : IDisposable
    {
        private int _disposed;

        internal EngineMaterialConstructionTarget Target { get; } = target;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Interlocked.CompareExchange(ref _current, null, this);
        }
    }
}
