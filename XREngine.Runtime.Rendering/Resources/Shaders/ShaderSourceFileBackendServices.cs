using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

/// <summary>Stores the host file backend used by shader source refresh jobs.</summary>
/// <remarks>
/// The backend and its generation are published together as one immutable box,
/// so captures and currency checks read them without a lock. Per-row scene
/// publication checks run these reads for every row on every frame.
/// </remarks>
public static class ShaderSourceFileBackendServices
{
    private static readonly object Gate = new();
    private static StrongBox<(IShaderSourceFileBackend? Backend, int Generation)> _installation = new((null, 0));

    public static IShaderSourceFileBackend? Current
    {
        get => Volatile.Read(ref _installation).Value.Backend;
        set
        {
            lock (Gate)
            {
                int generation = unchecked(_installation.Value.Generation + 1);
                Volatile.Write(ref _installation, new((value, generation)));
            }
        }
    }

    /// <summary>Captures one backend installation for an entire refresh job.</summary>
    internal static bool TryCapture(out IShaderSourceFileBackend? backend, out int generation)
    {
        (backend, generation) = Volatile.Read(ref _installation).Value;
        return backend is not null;
    }

    /// <summary>Rejects work from a replaced backend, including a reinstalled instance.</summary>
    internal static bool IsCurrent(IShaderSourceFileBackend? backend, int generation)
    {
        (IShaderSourceFileBackend? current, int currentGeneration) = Volatile.Read(ref _installation).Value;
        return ReferenceEquals(current, backend) && currentGeneration == generation;
    }
}
