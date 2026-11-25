using XREngine.Rendering.UI;
using XREngine.Input.Devices;

namespace XREngine.UnitTests.Rendering;

/// <summary>Counts target registration ownership without a physical input device.</summary>
public sealed class FocusRegistrationProbe : UIButtonComponent
{
    public int Registrations { get; private set; }
    public int Removals { get; private set; }

    public override void RegisterInput(IInputRegistration input)
    {
        if (input.Unregister)
            Removals++;
        else
            Registrations++;
    }
}
