using System.Security.Cryptography;
using System.Text.Json;
using MemoryPack;
using XREngine;
using XREngine.Core.Files;
using XREngine.Data.Core;

if (args.Length != 2)
    throw new ArgumentException("Usage: ShadowStartupVerifier <repo-root> <shadow-comparison-output>");

string repo = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
string validationRoot = Path.Combine(repo, "Build", "_AgentValidation");
string relative = Path.GetRelativePath(validationRoot, output);
if (relative is "." or ".." || Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    throw new InvalidDataException("The shadow comparison output must be below Build/_AgentValidation.");

static void RequireLocalDirectoryChain(string repo, string target)
{
    string relative = Path.GetRelativePath(repo, target);
    if (relative is ".." || Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new InvalidDataException("The shadow verifier path is outside its repository.");
    string current = repo;
    foreach (string segment in new[] { "." }.Concat(relative.Split(Path.DirectorySeparatorChar)))
    {
        current = Path.GetFullPath(Path.Combine(current, segment));
        DirectoryInfo directory = new(current);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0 || directory.LinkTarget is not null)
            throw new InvalidDataException($"The shadow verifier path has a linked or missing directory: {current}");
    }
}

static long RequireRegularFile(string file, long maximumBytes)
{
    FileInfo info = new(file);
    if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null
        || info.Length <= 0 || info.Length > maximumBytes)
        throw new InvalidDataException($"The shadow verifier requires a regular file: {file}");
    return info.Length;
}

RequireLocalDirectoryChain(repo, output);

static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

static (byte[] Bytes, string Hash) ReadStartup(string repo, string output, string state)
{
    string content = Path.Combine(output, state, "project", "Build", "browser-game", "content");
    RequireLocalDirectoryChain(repo, Path.Combine(content, "payload"));
    string manifestPath = Path.Combine(content, "manifest.json");
    RequireRegularFile(manifestPath, 2 * 1024 * 1024);
    using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
    JsonElement root = manifest.RootElement;
    if (root.GetProperty("startupSettings").GetString() != "/game/startup.asset")
        throw new InvalidDataException($"The shadow {state} bundle has the wrong startup settings path.");
    JsonElement? selected = null;
    foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
    {
        if (asset.GetProperty("path").GetString() != "/game/startup.asset")
            continue;
        if (selected is not null)
            throw new InvalidDataException($"The shadow {state} bundle has duplicate startup settings.");
        selected = asset;
    }
    if (selected is not JsonElement entry || entry.GetProperty("encoding").GetString() != "cooked-binary")
        throw new InvalidDataException($"The shadow {state} bundle has no cooked startup settings.");
    string hash = entry.GetProperty("hash").GetString() ?? "";
    if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)) || hash != hash.ToLowerInvariant()
        || entry.GetProperty("url").GetString() != $"payload/{hash}.bin")
        throw new InvalidDataException($"The shadow {state} startup settings have an invalid content hash.");
    int length = entry.GetProperty("bytes").GetInt32();
    if (length <= 0 || length > 1024 * 1024)
        throw new InvalidDataException($"The shadow {state} startup settings exceed the bounded size.");
    string payloadPath = Path.Combine(content, "payload", hash + ".bin");
    if (RequireRegularFile(payloadPath, 1024 * 1024) != length)
        throw new InvalidDataException($"The shadow {state} startup settings have the wrong file length.");
    byte[] bytes = File.ReadAllBytes(payloadPath);
    if (bytes.Length != length || Sha256(bytes) != hash)
        throw new InvalidDataException($"The shadow {state} startup settings differ from their manifest hash.");
    return (bytes, hash);
}

using IDisposable registration = BootstrapPublishedCookedAssetRegistration.Install();
using IDisposable suppression = XRObjectBase.SuppressObjectCacheRegistration();
(byte[] onBytes, string onHash) = ReadStartup(repo, output, "on");
(byte[] offBytes, string offHash) = ReadStartup(repo, output, "off");
using (FileStream file = new(Path.Combine(output, "published-startup-on.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
    file.Write(onBytes);
using (FileStream file = new(Path.Combine(output, "published-startup-off.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
    file.Write(offBytes);
CookedAssetBlob onBlob = CookedAssetEnvelope.Deserialize(onBytes);
CookedAssetBlob offBlob = CookedAssetEnvelope.Deserialize(offBytes);
if (onBlob.Format != CookedAssetFormat.RuntimeBinaryV1 || offBlob.Format != CookedAssetFormat.RuntimeBinaryV1
    || onBlob.TypeReference != offBlob.TypeReference)
    throw new InvalidDataException("The shadow startup settings use different cooked codecs or types.");
if (!CookedAssetEnvelope.Serialize(onBlob).AsSpan().SequenceEqual(onBytes)
    || !CookedAssetEnvelope.Serialize(offBlob).AsSpan().SequenceEqual(offBytes))
    throw new InvalidDataException("The shadow startup envelopes have noncanonical bytes.");
if (CookedAssetReader.LoadAsset(onBytes, typeof(GameStartupSettings), requireReferenceGraph: true) is not GameStartupSettings on
    || CookedAssetReader.LoadAsset(offBytes, typeof(GameStartupSettings), requireReferenceGraph: true) is not GameStartupSettings off)
    throw new InvalidDataException("The shadow startup payloads did not decode as GameStartupSettings.");
static bool HasExactSettingsShape(GameStartupSettings settings)
    => settings.GetType() == typeof(GameStartupSettings)
        && settings.BuildSettings?.GetType() == typeof(BuildSettings)
        && settings.DefaultUserSettings?.GetType() == typeof(UserSettings)
        && settings.ID != Guid.Empty
        && settings.BuildSettings.ID != Guid.Empty
        && settings.DefaultUserSettings.ID != Guid.Empty
        && new HashSet<Guid> { settings.ID, settings.BuildSettings.ID, settings.DefaultUserSettings.ID }.Count == 3;
if (!HasExactSettingsShape(on) || !HasExactSettingsShape(off))
    throw new InvalidDataException("The shadow startup settings have an invalid asset or identity shape.");

static (int Start, int End) FindIdSpan(GameStartupSettings settings, XRObjectBase target, string name)
{
    Guid original = target.ID;
    byte[] first;
    byte[] second;
    try
    {
        target.AdoptPersistentID(new Guid(Enumerable.Repeat((byte)0x11, 16).ToArray()));
        first = MemoryPackSerializer.Serialize(settings);
        target.AdoptPersistentID(new Guid(Enumerable.Repeat((byte)0x22, 16).ToArray()));
        second = MemoryPackSerializer.Serialize(settings);
    }
    finally
    {
        target.AdoptPersistentID(original);
    }
    if (first.Length != second.Length)
        throw new InvalidDataException($"The {name} ID changes the startup payload length.");
    int start = -1;
    int end = -1;
    int count = 0;
    for (int index = 0; index < first.Length; index++)
    {
        if (first[index] == second[index]) continue;
        if (start < 0) start = index;
        end = index;
        count++;
    }
    if (count != 16 || end - start != 15)
        throw new InvalidDataException($"The {name} ID is not one 16-byte MemoryPack field.");
    return (start, end);
}

static (int Start, int End)[] FindIdSpans(GameStartupSettings settings)
{
    (int Start, int End)[] spans =
    [
        FindIdSpan(settings, settings, "GameStartupSettings"),
        FindIdSpan(settings, settings.BuildSettings, "BuildSettings"),
        FindIdSpan(settings, settings.DefaultUserSettings, "DefaultUserSettings"),
    ];
    for (int first = 0; first < spans.Length; first++)
        for (int second = first + 1; second < spans.Length; second++)
            if (spans[first].Start <= spans[second].End && spans[second].Start <= spans[first].End)
                throw new InvalidDataException("The three startup ID fields overlap.");
    return spans;
}

static void RequireEqualOutsideIds(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second,
    (int Start, int End)[] spans, string context)
{
    if (first.Length != second.Length)
        throw new InvalidDataException($"The {context} startup payload lengths differ.");
    for (int index = 0; index < first.Length; index++)
    {
        if (spans.Any(span => index >= span.Start && index <= span.End)) continue;
        if (first[index] != second[index])
            throw new InvalidDataException($"The {context} startup payload differs outside the three transient settings asset IDs at byte {index}.");
    }
}

static void RequireRawIdentityShape(ReadOnlySpan<byte> payload, (int Start, int End)[] spans, string state)
{
    HashSet<Guid> identities = [];
    foreach ((int start, int end) in spans)
    {
        if (end >= payload.Length)
            throw new InvalidDataException($"The shadow {state} startup ID field exceeds its payload.");
        Guid identity = new(payload.Slice(start, 16));
        if (identity == Guid.Empty || !identities.Add(identity))
            throw new InvalidDataException($"The shadow {state} startup IDs are empty or duplicated in the raw payload.");
    }
}

(int Start, int End)[] onSpans = FindIdSpans(on);
(int Start, int End)[] offSpans = FindIdSpans(off);
if (!onSpans.SequenceEqual(offSpans))
    throw new InvalidDataException("The shadow startup ID fields have different MemoryPack layouts.");
RequireRawIdentityShape(onBlob.Payload, onSpans, "ON");
RequireRawIdentityShape(offBlob.Payload, offSpans, "OFF");
byte[] onRepeated = MemoryPackSerializer.Serialize(on);
byte[] offRepeated = MemoryPackSerializer.Serialize(off);
RequireEqualOutsideIds(onBlob.Payload, onRepeated, onSpans, "ON codec round-trip");
RequireEqualOutsideIds(offBlob.Payload, offRepeated, offSpans, "OFF codec round-trip");
RequireEqualOutsideIds(onBlob.Payload, offBlob.Payload, onSpans, "ON/OFF raw");
off.AdoptPersistentID(on.ID);
off.BuildSettings.AdoptPersistentID(on.BuildSettings.ID);
off.DefaultUserSettings.AdoptPersistentID(on.DefaultUserSettings.ID);
byte[] normalized = MemoryPackSerializer.Serialize(off);
if (!normalized.AsSpan().SequenceEqual(onRepeated))
    throw new InvalidDataException("The shadow startup settings differ outside the three transient settings asset IDs.");

string verification = Path.Combine(output, "startup-verification.json");
if (File.Exists(verification))
    throw new InvalidDataException("The shadow startup verification output must be a new local file.");
using (FileStream file = new(verification, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    JsonSerializer.Serialize(file, new
    {
        schema = 1,
        onStartupSha256 = onHash,
        offStartupSha256 = offHash,
        normalizedStartupSha256 = Sha256(onRepeated),
        normalizedFields = new[] { "GameStartupSettings.ID", "BuildSettings.ID", "DefaultUserSettings.ID" },
    });
Console.WriteLine($"Verified shadow startup settings: ON {onHash}, OFF {offHash}.");
