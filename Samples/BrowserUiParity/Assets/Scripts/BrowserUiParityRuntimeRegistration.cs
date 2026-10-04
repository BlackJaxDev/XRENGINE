using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using XREngine.Core.Files;

namespace BrowserUiParity;

/// <summary>Roots the game's types for ordinary runtime-binary world hydration.</summary>
[SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute is only intended to be used in application code or advanced source generator scenarios",
    Justification = "Game factories must be installed before the publisher or launcher hydrates the saved world.")]
public static class BrowserUiParityRuntimeRegistration
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
                RuntimeCookedBinarySerializer.RegisterRuntimeFactory(static () => new BrowserUiParityGameMode()),
                RuntimeCookedBinarySerializer.RegisterRuntimeFactory(static () => new BrowserUiParityPawnComponent()),
                RuntimeCookedBinarySerializer.RegisterRuntimeFactory(static () => new BrowserUiParityImageComponent()),
            ];
        }
    }
}
