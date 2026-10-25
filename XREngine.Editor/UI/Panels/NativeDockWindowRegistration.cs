using XREngine.Components;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Removes a dock host from the registry when its canvas leaves the scene.</summary>
internal sealed class NativeDockWindowRegistration : XRComponent
{
    private string? _key;
    private bool _supportsAllPanels;
    private bool _terminal;
    private ulong _registrationRevision;
    internal Func<string, bool>? ReceivePanel { get; private set; }
    internal Func<string, string, bool>? ReceivePanelAtStack { get; private set; }
    internal XRWindow? Window { get; private set; }
    private object? _ownerContext;
    internal ulong RegistrationRevision => _registrationRevision;

    internal bool TryGetRegistration(out string key, out object? ownerContext)
    {
        key = _key ?? string.Empty;
        ownerContext = _ownerContext;
        return !_terminal && IsActiveInHierarchy && _key is not null && ReceivePanel is not null &&
            (_ownerContext is not RuntimeWorld world || ReferenceEquals(SceneNode.World, world));
    }

    internal bool CanReceive(string panel)
        => _supportsAllPanels || NativeDockPanel.LegacyTools.Contains(panel);

    public void Configure(string key, Action<int> receivePanel)
    {
        Configure(key, (string panel) =>
        {
            int index = Array.IndexOf(NativeDockPanel.LegacyTools, panel);
            if (index < 0)
                return false;
            receivePanel(index);
            return true;
        }, null);
        _supportsAllPanels = false;
    }

    public void Configure(string key, Func<string, bool> receivePanel, object? ownerContext,
        XRWindow? window = null, Func<string, string, bool>? receivePanelAtStack = null)
    {
        if (_terminal) return;
        _registrationRevision++;
        if (_key is { } oldKey)
            NativeDockWindowRegistry.Unregister(oldKey, this);
        _key = key;
        _ownerContext = ownerContext;
        Window = window;
        _supportsAllPanels = true;
        ReceivePanel = receivePanel;
        ReceivePanelAtStack = receivePanelAtStack;
        if (IsActiveInHierarchy)
            NativeDockWindowRegistry.Register(key, this);
    }

    protected override void OnComponentActivated()
    {
        _registrationRevision++;
        base.OnComponentActivated();
        if (!_terminal && IsActiveInHierarchy && _key is not null)
            NativeDockWindowRegistry.Register(_key, this);
    }

    protected override void OnComponentDeactivated()
    {
        _registrationRevision++;
        if (_key is not null)
            NativeDockWindowRegistry.Unregister(_key, this);
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        _terminal = true;
        _registrationRevision++;
        if (_key is { } key)
            NativeDockWindowRegistry.Unregister(key, this);
        _key = null;
        _ownerContext = null;
        Window = null;
        ReceivePanel = null;
        ReceivePanelAtStack = null;
        base.OnDestroying();
    }
}
