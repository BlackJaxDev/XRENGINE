using System.Runtime.CompilerServices;

namespace XREngine.Input
{
    internal static class InputIntegrationModuleInitializer
    {
        [ModuleInitializer]
        internal static void Register()
        {
            RuntimePlayerControllerServices.DefaultLocalControllerType = typeof(LocalPlayerController);
            RuntimePlayerControllerServices.DefaultRemoteControllerType = typeof(RemotePlayerController);
        }
    }
}
