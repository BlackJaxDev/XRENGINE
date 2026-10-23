using XREngine.Components;

namespace XREngine.Editor;

/// <summary>Connects native dock hosts already attached to live editor windows.</summary>
internal static class NativeDockWindowRegistry
{
    private static readonly Dictionary<string, NativeDockWindowRegistration> Hosts = new(StringComparer.Ordinal);

    public static void Register(string key, NativeDockWindowRegistration host) => Hosts[key] = host;

    public static void Unregister(string key, NativeDockWindowRegistration host)
    {
        if (Hosts.TryGetValue(key, out NativeDockWindowRegistration? current) && ReferenceEquals(current, host))
            Hosts.Remove(key);
    }

    public static bool TryMoveToAnotherWindow(string sourceKey, int panel)
        => (uint)panel < (uint)NativeDockPanel.LegacyTools.Length &&
           TryMoveToAnotherWindow(sourceKey, NativeDockPanel.LegacyTools[panel]);

    public static bool TryMoveToAnotherWindow(string sourceKey, string panel)
    {
        if (!NativeDockPanel.All.Contains(panel))
            return false;
        foreach (var (key, host) in Hosts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (key != sourceKey && host.IsActiveInHierarchy && host.CanReceive(panel) &&
                host.ReceivePanel is { } receive)
            {
                receive(panel);
                return true;
            }
        }
        return false;
    }
}

/// <summary>Removes a dock host from the registry when its canvas leaves the scene.</summary>
internal sealed class NativeDockWindowRegistration : XRComponent
{
    private string? _key;
    private bool _supportsAllPanels;
    internal Action<string>? ReceivePanel { get; private set; }

    internal bool CanReceive(string panel)
        => _supportsAllPanels || NativeDockPanel.LegacyTools.Contains(panel);

    public void Configure(string key, Action<int> receivePanel)
    {
        Configure(key, (string panel) =>
        {
            int index = Array.IndexOf(NativeDockPanel.LegacyTools, panel);
            if (index >= 0)
                receivePanel(index);
        });
        _supportsAllPanels = false;
    }

    public void Configure(string key, Action<string> receivePanel)
    {
        _key = key;
        _supportsAllPanels = true;
        ReceivePanel = receivePanel;
        if (IsActiveInHierarchy)
            NativeDockWindowRegistry.Register(key, this);
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (_key is not null)
            NativeDockWindowRegistry.Register(_key, this);
    }

    protected override void OnComponentDeactivated()
    {
        if (_key is not null)
            NativeDockWindowRegistry.Unregister(_key, this);
        base.OnComponentDeactivated();
    }
}
