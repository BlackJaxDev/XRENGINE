using System.Threading;
using XREngine.Input.Devices;

namespace XREngine
{
    public static partial class Engine
    {
        /// <summary>
        /// Interface for accessing inputs.
        /// </summary>
        public static class Input
        {
            private static int _uiInputCaptured;

            /// <summary>
            /// Indicates whether a UI layer is currently consuming input that should bypass gameplay systems.
            /// </summary>
            public static bool IsUIInputCaptured => Volatile.Read(ref _uiInputCaptured) == 1;

            /// <summary>
            /// Sets whether a UI layer is actively consuming input for the current frame.
            /// </summary>
            /// <param name="captured">True when UI input should take precedence over gameplay input.</param>
            public static void SetUIInputCaptured(bool captured)
                => Volatile.Write(ref _uiInputCaptured, captured ? 1 : 0);

            public static InputInterface? Get(ELocalPlayerIndex index)
                => State.LocalPlayers[(int)index]?.Input;

            public static bool Key(ELocalPlayerIndex localPlayerIndex, EKey key, EButtonInputType type)
                => Get(localPlayerIndex)?.GetKeyState(key, type) ?? false;
            public static bool Button(ELocalPlayerIndex localPlayerIndex, EGamePadButton button, EButtonInputType type)
                => Get(localPlayerIndex)?.GetButtonState(button, type) ?? false;
            public static bool MouseButton(ELocalPlayerIndex localPlayerIndex, EMouseButton button, EButtonInputType type)
                => Get(localPlayerIndex)?.GetMouseButtonState(button, type) ?? false;
            public static bool AxisButton(ELocalPlayerIndex localPlayerIndex, EGamePadAxis axis, EButtonInputType type)
                => Get(localPlayerIndex)?.GetAxisState(axis, type) ?? false;

            public static float Axis(ELocalPlayerIndex localPlayerIndex, EGamePadAxis axis)
                => Get(localPlayerIndex)?.GetAxisValue(axis) ?? 0.0f;
            
            public static bool KeyReleased(ELocalPlayerIndex localPlayerIndex, EKey key)
                => Key(localPlayerIndex, key, EButtonInputType.Released);
            public static bool KeyPressed(ELocalPlayerIndex localPlayerIndex, EKey key)
                => Key(localPlayerIndex, key, EButtonInputType.Pressed);
            public static bool KeyHeld(ELocalPlayerIndex localPlayerIndex, EKey key)
                => Key(localPlayerIndex, key, EButtonInputType.Held);
            public static bool KeyDoublePressed(ELocalPlayerIndex localPlayerIndex, EKey key)
                => Key(localPlayerIndex, key, EButtonInputType.DoublePressed);

            public static bool ButtonReleased(ELocalPlayerIndex localPlayerIndex, EGamePadButton button)
                => Button(localPlayerIndex, button, EButtonInputType.Released);
            public static bool ButtonPressed(ELocalPlayerIndex localPlayerIndex, EGamePadButton button)
                => Button(localPlayerIndex, button, EButtonInputType.Pressed);
            public static bool ButtonHeld(ELocalPlayerIndex localPlayerIndex, EGamePadButton button)
                => Button(localPlayerIndex, button, EButtonInputType.Held);
            public static bool ButtonDoublePressed(ELocalPlayerIndex localPlayerIndex, EGamePadButton button)
                => Button(localPlayerIndex, button, EButtonInputType.DoublePressed);
            
            public static bool MouseButtonReleased(ELocalPlayerIndex localPlayerIndex, EMouseButton button)
                => MouseButton(localPlayerIndex, button, EButtonInputType.Released);
            public static bool MouseButtonPressed(ELocalPlayerIndex localPlayerIndex, EMouseButton button)
                => MouseButton(localPlayerIndex, button, EButtonInputType.Pressed);
            public static bool MouseButtonHeld(ELocalPlayerIndex localPlayerIndex, EMouseButton button)
                => MouseButton(localPlayerIndex, button, EButtonInputType.Held);
            public static bool MouseButtonDoublePressed(ELocalPlayerIndex localPlayerIndex, EMouseButton button)
                => MouseButton(localPlayerIndex, button, EButtonInputType.DoublePressed);
        }
    }
}
