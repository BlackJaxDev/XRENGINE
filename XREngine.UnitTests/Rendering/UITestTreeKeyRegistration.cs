using System;
using System.Collections.Generic;
using XREngine.Input.Devices;

namespace XREngine.UnitTests.Rendering;

internal sealed class UITestTreeKeyRegistration : IInputRegistration
{
    private readonly Dictionary<EKey, DelButtonState> _keys = [];

    public bool Unregister { get; set; }
    public int KeyCount => _keys.Count;
    public bool Press(EKey key)
    {
        if (!_keys.TryGetValue(key, out DelButtonState? callback))
            return false;
        callback(true);
        return true;
    }

    public void RegisterKeyStateChange(EKey key, DelButtonState callback)
    {
        if (Unregister)
        {
            if (_keys.TryGetValue(key, out DelButtonState? existing) && existing == callback)
                _keys.Remove(key);
            return;
        }
        if (!_keys.TryAdd(key, callback))
            throw new InvalidOperationException("A key was registered twice.");
    }

    public void RegisterMouseButtonEvent(EMouseButton button, EButtonInputType type, Action callback) { }
    public void RegisterMouseScroll(DelMouseScroll callback) { }
    public void RegisterKeyCharacter(Action<char> callback) { }
}
