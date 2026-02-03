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
    /// users can set <see cref="GameMode.DefaultPlayerControllerClass"/> and
    /// <see cref="GameMode.DefaultPlayerPawnClass"/> to arbitrary types.
    /// </summary>
    [XRTypeRedirect("XREngine.GameMode")]
    public class CustomGameMode : GameMode
    {
        public CustomGameMode() { }

        /// <summary>
        /// Controller type instantiated for local players when this GameMode ensures controller availability.
        /// Defaults to <see cref="LocalPlayerController"/>.
        /// </summary>
        [Description("Controller type instantiated for local players when this GameMode ensures controller availability.")]
        public Type? DefaultPlayerControllerClass
        {
            get => _defaultPlayerControllerClass;
            set
            {
                if (value is not null && !typeof(LocalPlayerController).IsAssignableFrom(value))
                    throw new ArgumentException("Default player controller must inherit from LocalPlayerController", nameof(value));

                SetField(ref _defaultPlayerControllerClass, value);
            }
        }

        /// <summary>
        /// Pawn component type spawned for local players when entering play. Defaults to <see cref="FlyingCameraPawnComponent"/>.
        /// </summary>
        [Description("Pawn component type spawned for local players when entering play.")]
        public Type? DefaultPlayerPawnClass
        {
            get => _defaultPlayerPawnClass;
            set
            {
                if (value is not null && !typeof(PawnComponent).IsAssignableFrom(value))
                    throw new ArgumentException("Default player pawn must inherit from PawnComponent", nameof(value));

                SetField(ref _defaultPlayerPawnClass, value);
            }
        }
    }
}
