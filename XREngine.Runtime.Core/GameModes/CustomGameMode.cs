using System;
using System.Collections.Generic;
using System.ComponentModel;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Input;

namespace XREngine
{
    /// <summary>
    /// Concrete version of <see cref="GameMode"/> that preserves the current behavior:
    /// users can set the default controller, pawn, and player user-interface component types.
    /// </summary>
    [XRTypeRedirect("XREngine.GameMode")]
    public class CustomGameMode : GameMode
    {
        public CustomGameMode() { }

        /// <summary>
        /// Controller type instantiated for local players when this GameMode ensures controller availability.
        /// Defaults to the runtime input integration's local player controller type.
        /// </summary>
        [Description("Controller type instantiated for local players when this GameMode ensures controller availability.")]
        public Type? DefaultPlayerControllerClass
        {
            get => _defaultPlayerControllerClass;
            set
            {
                if (value is not null && !typeof(IPawnController).IsAssignableFrom(value))
                    throw new ArgumentException("Default player controller must implement IPawnController", nameof(value));

                SetField(ref _defaultPlayerControllerClass, value);
            }
        }

        /// <summary>
        /// Pawn component type spawned for local players when entering play. 
        /// </summary>
        [Description("Pawn component type spawned for local players when entering play.")]
        public Type? DefaultPlayerPawnClass
        {
            get => _defaultPlayerPawnClass ?? RuntimeGameModeHostServices.Current?.DefaultPawnType;
            set
            {
                if (value is not null && !typeof(IRuntimeGameModePawn).IsAssignableFrom(value))
                    throw new ArgumentException("Default player pawn must implement IRuntimeGameModePawn", nameof(value));

                SetField(ref _defaultPlayerPawnClass, value);
            }
        }

        /// <summary>
        /// User-interface component type created and bound to each auto-spawned local player's camera.
        /// A null value means the game mode does not create a player UI.
        /// </summary>
        [Description("User-interface component type created for local players when entering play.")]
        public Type? DefaultPlayerUserInterfaceClass
        {
            get => _defaultPlayerUserInterfaceClass;
            set
            {
                if (value is not null &&
                    (!typeof(XRComponent).IsAssignableFrom(value) ||
                     !typeof(IRuntimeGameModeUserInterface).IsAssignableFrom(value)))
                {
                    throw new ArgumentException(
                        $"Default player user interface must derive from {nameof(XRComponent)} and implement " +
                        nameof(IRuntimeGameModeUserInterface),
                        nameof(value));
                }

                SetField(ref _defaultPlayerUserInterfaceClass, value);
            }
        }
    }
}
