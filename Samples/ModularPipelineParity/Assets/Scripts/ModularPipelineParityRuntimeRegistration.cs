using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using XREngine.Core.Files;

namespace ModularPipelineParity;

/// <summary>Registers concrete game types before saved world and cooked asset hydration.</summary>
[SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute is only intended to be used in application code or advanced source generator scenarios",
    Justification = "The game types must be available to the normal publisher and browser asset decoder.")]
public static class ModularPipelineParityRuntimeRegistration
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
                RuntimeCookedBinarySerializer.RegisterRuntimeFactory(static () => new ModularPipelineParityGameMode()),
                RuntimeCookedBinarySerializer.RegisterRuntimeFactory(static () => new ModularPipelineParityPawnComponent()),
                RuntimeCookedBinarySerializer.RegisterRuntimeFactory(static () => new ModularClearRenderPipeline()),
                RuntimeCookedBinarySerializer.RegisterRuntimeFactory(static () => new ModularQuadRenderPipeline()),
            ];
        }
    }
}
