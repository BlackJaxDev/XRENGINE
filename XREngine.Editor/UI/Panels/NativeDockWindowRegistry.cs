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
        => Hosts.TryGetValue(sourceKey, out NativeDockWindowRegistration? source) &&
            TryMoveToAnotherWindow(source, panel);

    public static bool TryMoveToAnotherWindow(NativeDockWindowRegistration source, string panel)
        => TransferToAnotherWindow(source, panel) == NativeDockTransferResult.Committed;

    public static NativeDockTransferResult TransferToAnotherWindow(NativeDockWindowRegistration source,
        string panel)
    {
        if (!NativeDockPanel.All.Contains(panel) || !source.TryGetRegistration(out string sourceKey,
                out object? context) || context is null ||
            !Hosts.TryGetValue(sourceKey, out NativeDockWindowRegistration? registeredSource) ||
            !ReferenceEquals(registeredSource, source))
            return NativeDockTransferResult.Rejected;
        ulong sourceRevision = source.RegistrationRevision;
        foreach (var (key, host) in Hosts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (key == sourceKey || !host.TryGetRegistration(out string destinationKey,
                    out object? destinationContext) || destinationKey != key ||
                !ReferenceEquals(context, destinationContext) || !host.CanReceive(panel) ||
                host.ReceivePanel is not { } receive)
                continue;
            ulong destinationRevision = host.RegistrationRevision;
            try
            {
                if (!receive(panel))
                    return NativeDockTransferResult.Rejected;
                bool committed = source.RegistrationRevision == sourceRevision &&
                    host.RegistrationRevision == destinationRevision &&
                    source.TryGetRegistration(out string currentSourceKey, out object? currentContext) &&
                    currentSourceKey == sourceKey && ReferenceEquals(currentContext, context) &&
                    Hosts.TryGetValue(sourceKey, out registeredSource) &&
                    ReferenceEquals(registeredSource, source) &&
                    host.TryGetRegistration(out string currentDestinationKey, out object? currentDestinationContext) &&
                    currentDestinationKey == key && ReferenceEquals(currentDestinationContext, context) &&
                    Hosts.TryGetValue(key, out NativeDockWindowRegistration? currentDestination) &&
                    ReferenceEquals(currentDestination, host) && ReferenceEquals(host.ReceivePanel, receive);
                return committed ? NativeDockTransferResult.Committed : NativeDockTransferResult.Rejected;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, "[NativeDock] Destination rejected the panel transfer.");
                return NativeDockTransferResult.Rejected;
            }
        }
        return NativeDockTransferResult.NoTarget;
    }
}
