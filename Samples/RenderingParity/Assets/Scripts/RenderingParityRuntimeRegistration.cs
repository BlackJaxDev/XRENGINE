using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using XREngine.Core.Files;

namespace RenderingParity;

/// <summary>Roots the game's concrete types for ordinary runtime-binary world hydration.</summary>
[SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute is only intended to be used in application code or advanced source generator scenarios",
    Justification = "Game factories must be installed before the publisher or launcher hydrates the saved world.")]
public static class RenderingParityRuntimeRegistration
{
    private static readonly object Sync = new();
    private static IDisposable[]? _registrations;

    [ModuleInitializer]
    public static void Register()
    {
        lock (Sync)
        {
            if (_registrations is not null)
                return;
            _registrations =
            [
                RuntimeCookedBinarySerializer.RegisterRuntimeFactory(static () => new RenderingParityGameMode()),
                RuntimeCookedBinarySerializer.RegisterRuntimeFactory(static () => new RenderingParityAnimationComponent()),
                RuntimeCookedBinarySerializer.RegisterRuntimeFactory(static () => new RenderingParityPawnComponent()),
            ];
        }
    }
}
