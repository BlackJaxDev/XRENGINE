using OpenVRAction = OpenVR.NET.Input.Action;

namespace XREngine.Input;

public interface IRuntimeVrInputServices
{
    event System.Action<Dictionary<string, Dictionary<string, OpenVRAction>>>? ActionsChanged;
    Dictionary<string, Dictionary<string, OpenVRAction>> Actions { get; }
}

public static class RuntimeVrInputServices
{
    private static readonly DefaultRuntimeVrInputServices Default = new();
    private static IRuntimeVrInputServices _current = Default;
    private static event System.Action<Dictionary<string, Dictionary<string, OpenVRAction>>>? StaticActionsChanged;

    static RuntimeVrInputServices()
    {
        _current.ActionsChanged += ForwardActionsChanged;
    }

    public static IRuntimeVrInputServices Current
    {
        get => _current;
        set
        {
            var next = value ?? Default;
            if (ReferenceEquals(_current, next))
                return;

            _current.ActionsChanged -= ForwardActionsChanged;
            _current = next;
            _current.ActionsChanged += ForwardActionsChanged;
        }
    }

    public static Dictionary<string, Dictionary<string, OpenVRAction>> Actions
        => Current.Actions;

    public static event System.Action<Dictionary<string, Dictionary<string, OpenVRAction>>>? ActionsChanged
    {
        add => StaticActionsChanged += value;
        remove => StaticActionsChanged -= value;
    }

    private static void ForwardActionsChanged(Dictionary<string, Dictionary<string, OpenVRAction>> actions)
        => StaticActionsChanged?.Invoke(actions);

    private sealed class DefaultRuntimeVrInputServices : IRuntimeVrInputServices
    {
        public event System.Action<Dictionary<string, Dictionary<string, OpenVRAction>>>? ActionsChanged
        {
            add { }
            remove { }
        }

        public Dictionary<string, Dictionary<string, OpenVRAction>> Actions { get; } = [];
    }
}