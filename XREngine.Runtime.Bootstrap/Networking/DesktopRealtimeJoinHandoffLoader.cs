using System.Text.Json;
using XREngine.Core.Files;
using XREngine.Networking;

namespace XREngine;

/// <summary>Reads a desktop realtime join handoff and applies it to startup settings.</summary>
public static class DesktopRealtimeJoinHandoffLoader
{
    /// <summary>Loads and applies an optional desktop launch handoff.</summary>
    public static bool TryApplyFromEnvironment(
        GameStartupSettings settings,
        out RealtimeJoinHandoffPayload? payload,
        out string? source)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!TryReadFromEnvironment(out payload, out source))
            return false;

        if (payload is not null)
            RealtimeJoinHandoff.ApplyToSettings(settings, payload);
        return true;
    }

    /// <summary>Loads an optional desktop launch handoff without applying startup settings.</summary>
    public static bool TryReadFromEnvironment(out RealtimeJoinHandoffPayload? payload, out string? source)
    {
        payload = null;
        source = null;

        string? payloadPath = GetOptionalEnvironmentValue(RealtimeJoinHandoffContract.PayloadFileEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(payloadPath))
        {
            if (OperatingSystem.IsBrowser()
                || DirectStorageIO.Source is { SupportsSynchronousReads: false } or IRuntimeAssetCatalog)
                throw new NotSupportedException(
                    "RealtimeJoinHandoff.FileSourceUnavailable: this host requires the inline realtime join payload instead of a handoff file.");

            string resolvedPath = Path.GetFullPath(payloadPath);
            if (!File.Exists(resolvedPath))
                throw new FileNotFoundException("Realtime join handoff payload file was not found.", resolvedPath);

            payload = DeserializePayload(File.ReadAllText(resolvedPath), resolvedPath);
            source = $"{RealtimeJoinHandoffContract.PayloadFileEnvironmentVariable}={resolvedPath}";
            return true;
        }

        string? payloadJson = GetOptionalEnvironmentValue(RealtimeJoinHandoffContract.PayloadEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(payloadJson))
            return false;

        payload = DeserializePayload(payloadJson, RealtimeJoinHandoffContract.PayloadEnvironmentVariable);
        source = RealtimeJoinHandoffContract.PayloadEnvironmentVariable;
        return true;
    }

    private static RealtimeJoinHandoffPayload DeserializePayload(string json, string source)
    {
        try
        {
            return JsonSerializer.Deserialize(json, DesktopRealtimeJoinHandoffJsonContext.Default.RealtimeJoinHandoffPayload)
                ?? throw new InvalidOperationException("Realtime handoff payload was empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Realtime handoff payload from {source} is not valid JSON.", ex);
        }
    }

    private static string? GetOptionalEnvironmentValue(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
