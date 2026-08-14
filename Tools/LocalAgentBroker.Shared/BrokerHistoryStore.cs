using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker.Shared;

/// <summary>
/// Atomic file-backed storage shared by independently hosted broker and tray processes.
/// </summary>
public sealed class BrokerHistoryStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly BrokerUiPaths _paths;

    public BrokerHistoryStore(BrokerUiPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        Directory.CreateDirectory(_paths.RunsDirectory);
    }

    public BrokerUiPaths Paths => _paths;

    public void SaveRecord(BrokerHistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsValidRunId(record.RunId))
            throw new ArgumentException("Broker history run IDs must be 32 hexadecimal characters.");

        WriteAtomically(RecordPath(record.RunId), JsonSerializer.Serialize(record, s_jsonOptions));
    }

    public IReadOnlyList<BrokerHistoryRecord> LoadRecords()
    {
        if (!Directory.Exists(_paths.RunsDirectory))
            return [];

        var records = new List<BrokerHistoryRecord>();
        foreach (string path in Directory.EnumerateFiles(_paths.RunsDirectory, "*.json"))
        {
            try
            {
                string json = File.ReadAllText(path);
                BrokerHistoryRecord? record = JsonSerializer.Deserialize<BrokerHistoryRecord>(
                    json,
                    s_jsonOptions);
                if (record is not null && IsValidRunId(record.RunId))
                    records.Add(record);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or JsonException)
            {
                // A broker may be replacing this record or an older file may be malformed.
                // The next refresh retries it without disrupting the rest of the history.
            }
        }

        return records
            .OrderByDescending(static record => record.CreatedUtc)
            .ToArray();
    }

    public BrokerUiSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_paths.SettingsPath))
                return new BrokerUiSettings();
            string json = File.ReadAllText(_paths.SettingsPath);
            return (JsonSerializer.Deserialize<BrokerUiSettings>(json, s_jsonOptions)
                ?? new BrokerUiSettings()).Normalize();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return new BrokerUiSettings();
        }
    }

    public void SaveSettings(BrokerUiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(_paths.RootDirectory);
        WriteAtomically(
            _paths.SettingsPath,
            JsonSerializer.Serialize(settings.Normalize(), s_jsonOptions));
    }

    public int DeleteTerminalRecordsOlderThan(TimeSpan age)
    {
        if (age <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(age));

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - age;
        int deleted = 0;
        foreach (BrokerHistoryRecord record in LoadRecords())
        {
            if (record.IsActive || record.UpdatedUtc >= cutoff)
                continue;
            try
            {
                File.Delete(RecordPath(record.RunId));
                deleted++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A concurrent writer or reader still owns the record. Retry on the next cleanup pass.
            }
        }

        return deleted;
    }

    public bool DeleteRecord(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        try
        {
            File.Delete(RecordPath(runId));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string RecordPath(string runId)
    {
        if (!IsValidRunId(runId))
            throw new ArgumentException("Broker history run IDs must be 32 hexadecimal characters.");
        return Path.Combine(_paths.RunsDirectory, $"{runId}.json");
    }

    private static bool IsValidRunId(string runId)
        => runId.Length == 32
            && runId.All(static character => char.IsAsciiHexDigit(character));

    private static void WriteAtomically(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        string temporaryPath = $"{path}.tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of a never-published temporary file.
            }
        }
    }
}
