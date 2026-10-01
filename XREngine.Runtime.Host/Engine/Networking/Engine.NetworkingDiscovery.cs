using System;

namespace XREngine
{
    public static partial class Engine
    {
        /// <summary>
        /// Public helper to (re)configure networking at runtime using the existing initialization path.
        /// </summary>
        /// <param name="startupSettings">Networking settings to apply.</param>
        /// <returns>The active networking manager, if networking was enabled.</returns>
        public static BaseNetworkingManager? ConfigureNetworking(GameStartupSettings startupSettings)
        {
            ArgumentNullException.ThrowIfNull(startupSettings);
            InitializeNetworking(startupSettings);
            return Networking;
        }
    }
}
