using System.Runtime.InteropServices.JavaScript;
using XREngine.Networking;

namespace XREngine.Browser;

public static partial class BrowserEngineExports
{
    private static bool _networkPageActive = true;
    internal static bool IsNetworkPageActive => _networkPageActive;

    /// <summary>
    /// Consumes the shared handoff JSON from the application's authenticated control plane.
    /// Never pass a URL, persist the JSON, or include it in diagnostics. Completion is not gameplay readiness.
    /// </summary>
    [JSExport]
    public static async Task<string> ConnectWebSocketAsync(string handoffJson, bool requireVoice)
    {
        if (requireVoice)
            throw new NotSupportedException("BrowserNetwork.VoiceUnsupported: microphone capture, voice transport and mixing are not installed.");
        if (!_networkPageActive || _session is not { IsRunning: true } session)
            throw new InvalidOperationException("BrowserNetwork.InactiveWorld: join requires an active loaded engine world.");
        RealtimeJoinHandoffPayload handoff = BrowserRealtimeHandoff.Parse(handoffJson);
        try
        {
            return await session.ConnectNetworkAsync(handoff);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("BrowserNetwork.Suspended: request fresh admission after the page and world resume.");
        }
        catch (Exception)
        {
            // Browser interop must not serialize underlying exceptions or a
            // credential-bearing payload into the page's startup diagnostics.
            throw new InvalidOperationException("BrowserNetwork.JoinFailed: verify the loaded package, build and fresh managed admission before retrying.");
        }
        finally
        {
            handoff.AdmissionSecret = null;
            handoff.SessionToken = null;
        }
    }

    /// <summary>Reports local, connecting, authenticating, synchronizing, ready, suspended, or failed.</summary>
    [JSExport]
    public static string GetNetworkState() => _session?.NetworkState ?? "local";

    [JSExport]
    public static bool IsNetworkGameplayReady() => _session?.NetworkState == "ready";

    [JSExport]
    public static bool IsVoiceSupported() => false;

    [JSExport]
    public static void SuspendNetwork() => _session?.SuspendNetwork();

    /// <summary>Driven by visibility, page-cache, and browser freeze events; reactivation never retries old credentials.</summary>
    [JSExport]
    public static void SetNetworkPageActive(bool active)
    {
        _networkPageActive = active;
        if (!active)
            _session?.SuspendNetwork();
    }
}
