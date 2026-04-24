using System;
using System.Collections.Generic;
using MemoryPack;

namespace XREngine.Networking;

public enum AdmissionFailureReason : byte
{
    None = 0,
    InvalidRequest = 1,
    SessionNotFound = 2,
    SessionFull = 3,
    BuildVersionMismatch = 4,
    WorldAssetMismatch = 5,
    Unauthorized = 6,
    TicketExpired = 7,
    RateLimited = 8,
}

public enum RealtimeTransportKind : byte
{
    NativeUdp = 0,
}

[MemoryPackable]
public sealed partial class WorldAssetIdentity
{
    public string WorldId { get; set; } = string.Empty;
    public string RevisionId { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public int AssetSchemaVersion { get; set; } = 1;
    public string RequiredBuildVersion { get; set; } = "dev";
    public Dictionary<string, string> Metadata { get; set; } = [];

    public bool IsSameAssetAs(WorldAssetIdentity? other)
    {
        if (other is null)
            return false;

        return string.Equals(WorldId, other.WorldId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(RevisionId, other.RevisionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeHash(ContentHash), NormalizeHash(other.ContentHash), StringComparison.OrdinalIgnoreCase)
            && AssetSchemaVersion == other.AssetSchemaVersion;
    }

    public bool IsBuildCompatible(string? buildVersion)
        => string.IsNullOrWhiteSpace(RequiredBuildVersion)
            || string.Equals(RequiredBuildVersion, "dev", StringComparison.OrdinalIgnoreCase)
            || string.Equals(buildVersion, "dev", StringComparison.OrdinalIgnoreCase)
            || string.Equals(RequiredBuildVersion, buildVersion, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return string.Empty;

        string normalized = hash.Trim();
        int separator = normalized.IndexOf(':');
        if (separator >= 0)
            normalized = normalized[(separator + 1)..];

        return normalized.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }
}

[MemoryPackable]
public sealed partial class RealtimeEndpointDescriptor
{
    public RealtimeTransportKind Transport { get; set; } = RealtimeTransportKind.NativeUdp;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public string ProtocolVersion { get; set; } = "dev";
    public Dictionary<string, string> Metadata { get; set; } = [];
}

public sealed class RealtimeJoinHandoffPayload
{
    public Guid? SessionId { get; set; }
    public string? SessionToken { get; set; }
    public RealtimeEndpointDescriptor? Endpoint { get; set; }
    public WorldAssetIdentity? WorldAsset { get; set; }
}
