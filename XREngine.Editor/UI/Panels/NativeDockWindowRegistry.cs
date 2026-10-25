using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Editor;

/// <summary>Connects native dock hosts already attached to live editor windows.</summary>
internal static class NativeDockWindowRegistry
{
    private static readonly Dictionary<string, NativeDockWindowRegistration> Hosts = new(StringComparer.Ordinal);

    public static void Register(string key, NativeDockWindowRegistration host) => Hosts[key] = host;

    internal static bool IsCurrentHost(string key, NativeDockWindowRegistration host)
        => Hosts.TryGetValue(key, out NativeDockWindowRegistration? current) &&
            ReferenceEquals(current, host);

    /// <summary>Captures native window lifetimes before a dock drag starts.</summary>
    internal static NativeDockDragWindowAdmission[] CaptureDragWindows(
        NativeDockWindowRegistration source)
    {
        if (!source.TryGetRegistration(out string sourceKey, out object? sourceContext) ||
            sourceContext is null || !IsCurrentHost(sourceKey, source))
            return [];
        List<NativeDockDragWindowAdmission> admissions = [];
        foreach ((string key, NativeDockWindowRegistration host) in Hosts)
        {
            if (!host.TryGetRegistration(out string registeredKey, out object? context) ||
                key != registeredKey || !ReferenceEquals(sourceContext, context) ||
                host.SceneNode is not { IsDestroyed: false, IsDestroyQueued: false } root ||
                host.Window is not { DesktopWindowBackend: { } backend } window ||
                backend.OperatingSystemWindowHandle == 0)
                continue;
            admissions.Add(new NativeDockDragWindowAdmission(host, host.RegistrationRevision,
                ObjectConstructionIdentity.Capture(host), ObjectConstructionIdentity.Capture(root),
                window, backend,
                backend.LifetimeGeneration, backend.OperatingSystemWindowHandle, key, context));
        }
        return admissions.ToArray();
    }

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
        ObjectConstructionIdentity sourceIdentity = ObjectConstructionIdentity.Capture(source);
        ObjectConstructionIdentity sourceRootIdentity =
            ObjectConstructionIdentity.Capture(source.SceneNode);
        foreach (var (key, host) in Hosts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (key == sourceKey || !host.TryGetRegistration(out string destinationKey,
                    out object? destinationContext) || destinationKey != key ||
                !ReferenceEquals(context, destinationContext) || !host.CanReceive(panel) ||
                host.ReceivePanel is not { } receive)
                continue;
            ulong destinationRevision = host.RegistrationRevision;
            ObjectConstructionIdentity destinationIdentity = ObjectConstructionIdentity.Capture(host);
            ObjectConstructionIdentity destinationRootIdentity =
                ObjectConstructionIdentity.Capture(host.SceneNode);
            Func<string, NativeDockPanelTransferState?>? capture = source.CapturePanelState;
            Func<string, NativeDockPanelTransferState, bool>? receiveState = host.ReceivePanelState;
            try
            {
                NativeDockPanelTransferState? state = capture?.Invoke(panel);
                if (capture is not null &&
                    (state is null || !state.Value.IsCurrent))
                    return NativeDockTransferResult.Rejected;
                if (state is { } stateForPanel &&
                    NativeDockPanelStateValues.ScrollIndex(stateForPanel.Values.Panel) >= 0 &&
                    receiveState is null)
                    continue;
                if (source.RegistrationRevision != sourceRevision ||
                    host.RegistrationRevision != destinationRevision ||
                    !sourceIdentity.IsCurrent || !sourceRootIdentity.IsCurrent ||
                    !destinationIdentity.IsCurrent || !destinationRootIdentity.IsCurrent ||
                    !ReferenceEquals(source.SceneNode, sourceRootIdentity.Target) ||
                    !ReferenceEquals(host.SceneNode, destinationRootIdentity.Target) ||
                    !ReferenceEquals(source.CapturePanelState, capture) ||
                    !ReferenceEquals(host.ReceivePanelState, receiveState) ||
                    !ReferenceEquals(host.ReceivePanel, receive) ||
                    !source.TryGetRegistration(out string verifiedSourceKey,
                        out object? verifiedSourceContext) ||
                    verifiedSourceKey != sourceKey ||
                    !ReferenceEquals(verifiedSourceContext, context) ||
                    !IsCurrentHost(sourceKey, source) ||
                    !host.TryGetRegistration(out string verifiedDestinationKey,
                        out object? verifiedDestinationContext) ||
                    verifiedDestinationKey != key ||
                    !ReferenceEquals(verifiedDestinationContext, context) ||
                    !IsCurrentHost(key, host))
                    return NativeDockTransferResult.Rejected;
                bool received = state is { } captured && receiveState is not null
                    ? receiveState(panel, captured) : receive(panel);
                if (!received || state is { } receivedState && !receivedState.IsCurrent)
                    return NativeDockTransferResult.Rejected;
                bool committed = source.RegistrationRevision == sourceRevision &&
                    host.RegistrationRevision == destinationRevision &&
                    sourceIdentity.IsCurrent && sourceRootIdentity.IsCurrent &&
                    destinationIdentity.IsCurrent && destinationRootIdentity.IsCurrent &&
                    ReferenceEquals(source.SceneNode, sourceRootIdentity.Target) &&
                    ReferenceEquals(host.SceneNode, destinationRootIdentity.Target) &&
                    ReferenceEquals(source.CapturePanelState, capture) &&
                    ReferenceEquals(host.ReceivePanelState, receiveState) &&
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

    /// <summary>Transfers to the exact dock host and stack under the released pointer.</summary>
    internal static NativeDockTransferResult TransferToWindow(
        NativeDockDragWindowAdmission sourceWindow,
        NativeDockDragWindowAdmission destinationWindow, string panel, string stackId)
    {
        if (!sourceWindow.IsCurrent || !destinationWindow.IsCurrent)
            return NativeDockTransferResult.Rejected;
        NativeDockTransferResult result = TransferToWindow(sourceWindow.Registration,
            destinationWindow.Registration, panel, stackId);
        return result == NativeDockTransferResult.Committed &&
            sourceWindow.IsCurrent && destinationWindow.IsCurrent
                ? NativeDockTransferResult.Committed : NativeDockTransferResult.Rejected;
    }

    internal static NativeDockTransferResult TransferToWindow(NativeDockWindowRegistration source,
        NativeDockWindowRegistration destination, string panel, string stackId)
    {
        if (!NativeDockPanel.All.Contains(panel) || string.IsNullOrEmpty(stackId) ||
            ReferenceEquals(source, destination) ||
            !source.TryGetRegistration(out string sourceKey, out object? context) ||
            context is null || !IsCurrentHost(sourceKey, source) ||
            !destination.TryGetRegistration(out string destinationKey, out object? destinationContext) ||
            destinationKey == sourceKey || !ReferenceEquals(context, destinationContext) ||
            !IsCurrentHost(destinationKey, destination) || !destination.CanReceive(panel) ||
            destination.ReceivePanelAtStack is not { } receive)
            return NativeDockTransferResult.Rejected;
        ulong sourceRevision = source.RegistrationRevision;
        ulong destinationRevision = destination.RegistrationRevision;
        ObjectConstructionIdentity sourceIdentity = ObjectConstructionIdentity.Capture(source);
        ObjectConstructionIdentity sourceRootIdentity =
            ObjectConstructionIdentity.Capture(source.SceneNode);
        ObjectConstructionIdentity destinationIdentity = ObjectConstructionIdentity.Capture(destination);
        ObjectConstructionIdentity destinationRootIdentity =
            ObjectConstructionIdentity.Capture(destination.SceneNode);
        Func<string, NativeDockPanelTransferState?>? capture = source.CapturePanelState;
        Func<string, string, NativeDockPanelTransferState, bool>? receiveState =
            destination.ReceivePanelStateAtStack;
        try
        {
            NativeDockPanelTransferState? state = capture?.Invoke(panel);
            if (capture is not null &&
                (state is null || !state.Value.IsCurrent))
                return NativeDockTransferResult.Rejected;
            if (state is { } stateForPanel &&
                NativeDockPanelStateValues.ScrollIndex(stateForPanel.Values.Panel) >= 0 &&
                receiveState is null)
                return NativeDockTransferResult.Rejected;
            if (source.RegistrationRevision != sourceRevision ||
                destination.RegistrationRevision != destinationRevision ||
                !sourceIdentity.IsCurrent || !sourceRootIdentity.IsCurrent ||
                !destinationIdentity.IsCurrent || !destinationRootIdentity.IsCurrent ||
                !ReferenceEquals(source.SceneNode, sourceRootIdentity.Target) ||
                !ReferenceEquals(destination.SceneNode, destinationRootIdentity.Target) ||
                !ReferenceEquals(source.CapturePanelState, capture) ||
                !ReferenceEquals(destination.ReceivePanelStateAtStack, receiveState) ||
                !ReferenceEquals(destination.ReceivePanelAtStack, receive) ||
                !source.TryGetRegistration(out string verifiedSourceKey,
                    out object? verifiedSourceContext) ||
                verifiedSourceKey != sourceKey ||
                !ReferenceEquals(verifiedSourceContext, context) ||
                !IsCurrentHost(sourceKey, source) ||
                !destination.TryGetRegistration(out string verifiedDestinationKey,
                    out object? verifiedDestinationContext) ||
                verifiedDestinationKey != destinationKey ||
                !ReferenceEquals(verifiedDestinationContext, context) ||
                !IsCurrentHost(destinationKey, destination))
                return NativeDockTransferResult.Rejected;
            bool received = state is { } captured && receiveState is not null
                    ? receiveState(panel, stackId, captured) : receive(panel, stackId);
            if (!received || state is { } receivedState && !receivedState.IsCurrent)
                return NativeDockTransferResult.Rejected;
            return source.RegistrationRevision == sourceRevision &&
                destination.RegistrationRevision == destinationRevision &&
                sourceIdentity.IsCurrent && sourceRootIdentity.IsCurrent &&
                destinationIdentity.IsCurrent && destinationRootIdentity.IsCurrent &&
                ReferenceEquals(source.SceneNode, sourceRootIdentity.Target) &&
                ReferenceEquals(destination.SceneNode, destinationRootIdentity.Target) &&
                ReferenceEquals(source.CapturePanelState, capture) &&
                ReferenceEquals(destination.ReceivePanelStateAtStack, receiveState) &&
                source.TryGetRegistration(out string currentSourceKey, out object? currentContext) &&
                currentSourceKey == sourceKey && ReferenceEquals(currentContext, context) &&
                IsCurrentHost(sourceKey, source) &&
                destination.TryGetRegistration(out string currentDestinationKey,
                    out object? currentDestinationContext) &&
                currentDestinationKey == destinationKey &&
                ReferenceEquals(currentDestinationContext, context) &&
                IsCurrentHost(destinationKey, destination) &&
                ReferenceEquals(destination.ReceivePanelAtStack, receive)
                    ? NativeDockTransferResult.Committed : NativeDockTransferResult.Rejected;
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, "[NativeDock] Destination rejected the pointed panel transfer.");
            return NativeDockTransferResult.Rejected;
        }
    }
}
