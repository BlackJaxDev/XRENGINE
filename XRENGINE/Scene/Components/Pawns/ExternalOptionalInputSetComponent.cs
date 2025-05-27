using XREngine.Input.Devices;

namespace XREngine.Components
{
    /// <summary>
    /// Represents a component that allows external registration of optional inputs.
    /// </summary>
    /// <remarks>This class provides a mechanism to register inputs externally by raising the <see
    /// cref="OnRegisterInput"/> event. It extends the functionality of the <see cref="OptionalInputSetComponent"/> base
    /// class.</remarks>
    public class ExternalOptionalInputSetComponent : OptionalInputSetComponent
    {
        public event Action<InputInterface>? OnRegisterInput;
        public override void RegisterInput(InputInterface input)
            => OnRegisterInput?.Invoke(input);
    }
}
