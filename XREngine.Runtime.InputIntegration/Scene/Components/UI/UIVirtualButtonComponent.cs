using System.Numerics;
using XREngine.Input.Devices;

namespace XREngine.Components;

/// <summary>A captured touch or mouse button mapped to an existing logical gamepad button.</summary>
public sealed class UIVirtualButtonComponent : UIVirtualInputComponent
{
    private EGamePadButton _button = EGamePadButton.FaceDown;

    public EGamePadButton Button
    {
        get => _button;
        set
        {
            if ((uint)value >= 14)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (SetField(ref _button, value))
                CancelPointer();
        }
    }

    internal override ushort ButtonMask => (ushort)(1 << (int)_button);
    internal override void UpdatePointer(Vector2 localPosition) { }
}
