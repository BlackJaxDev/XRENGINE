using XREngine.Input.Devices;

namespace XREngine.Components
{
    /// <summary>
    /// Used by PawnComponents to register optional input sets.
    /// This component can be a sibling of the PawnComponent or put into its list of sets to register.
    /// </summary>
    public abstract class OptionalInputSetComponent : XRComponent
    {
        public abstract void RegisterInput(InputInterface input);
    }
}
