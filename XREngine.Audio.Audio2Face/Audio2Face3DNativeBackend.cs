using XREngine.Core.Files;

namespace XREngine.Components;

/// <summary>Registers native Audio2Face scene integration without loading the native bridge.</summary>
public static class Audio2Face3DNativeBackend
{
    public static void Register()
    {
        CookedBinarySerializer.RegisterRuntimeFactory(static () => new Audio2Face3DNativeBridgeComponent());
        Audio2Face3DNativeBridgeComponent.RegisterModuleAdapter();
    }
}
