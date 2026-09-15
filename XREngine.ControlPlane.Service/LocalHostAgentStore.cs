using System.Text.Json;
using System.Security.Cryptography;

namespace XREngine.ControlPlane.Service;

/// <summary>Single-writer durable ownership ledger for the local host agent. It never stores worker bearer tokens.</summary>
internal sealed class LocalHostAgentStore : IDisposable
{
    private const int MaximumCheckpointBytes = 8 * 1024 * 1024;
    private readonly FileStream _lock;
    private readonly string _journalPath;
    private readonly string _checkpointPath;
    private readonly object _sync = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalHostAgentStore"/> class, setting up the necessary file paths and lock for the local host agent's durable storage.
    /// </summary>
    /// <param name="options">The local service options containing the host agent data root path.</param>
    public LocalHostAgentStore(LocalServiceOptions options)
    {
        string root = options.HostAgentDataRoot;
        Directory.CreateDirectory(root);
        _lock = new FileStream(Path.Combine(root, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _journalPath = Path.Combine(root, "ownership.journal.jsonl");
        _checkpointPath = Path.Combine(root, "registry.checkpoint.bin");
    }

    /// <summary>
    /// Saves the current state of the registry to a durable checkpoint, ensuring that it does not exceed the maximum allowed size.
    /// </summary>
    /// <param name="registry">The in-memory control plane registry whose state is to be saved.</param>
    /// <exception cref="InvalidOperationException">Thrown if the registry checkpoint exceeds the maximum allowed size.</exception>
    public void SaveRegistry(InMemoryControlPlane registry)
    {
        lock (_sync)
        {
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(registry.ExportDurableSnapshot(), ServiceJson.Options);
            if (plain.Length > MaximumCheckpointBytes)
                throw new InvalidOperationException("Registry checkpoint exceeds the local durability limit.");
            
            byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            try
            {
                using (var stream = new FileStream($"{_checkpointPath}.tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(protectedBytes); 
                    stream.Flush(true); 
                }

                File.Move($"{_checkpointPath}.tmp", _checkpointPath, true);
            }
            finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(protectedBytes); }
        }
    }

    /// <summary>
    /// Restores the state of the registry from the durable checkpoint, if it exists.
    /// </summary>
    /// <param name="registry">The in-memory control plane registry to restore the state into.</param>
    /// <exception cref="InvalidOperationException">Thrown if the registry checkpoint is invalid or cannot be restored.</exception>
    public void RestoreRegistry(InMemoryControlPlane registry)
    {
        if (!File.Exists(_checkpointPath))
            return;
        
        FileInfo checkpoint = new(_checkpointPath);
        if (checkpoint.Length is <= 0 or > MaximumCheckpointBytes * 2L)
            throw new InvalidOperationException("Registry checkpoint size is invalid.");
        
        byte[] protectedBytes = File.ReadAllBytes(_checkpointPath); 
        byte[] plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);

        try
        {
            registry.ImportDurableSnapshot(JsonSerializer.Deserialize<DurableControlPlaneSnapshot>(plain, ServiceJson.Options) 
                ?? throw new InvalidOperationException("Invalid registry checkpoint.")); 
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// Records that a worker has started execution on the local host.
    /// </summary>
    /// <param name="worker">The worker execution instance that has started.</param>
    public void RecordStarted(WorkerExecution worker)
    {
        if (worker.Process is null)
            return;
        
        Append(new LocalHostAgentOwnershipRecord(
            worker.Launch.InstanceId, 
            worker.Launch.Generation, 
            worker.Process.Id,
            worker.Process.StartTime.ToUniversalTime(), 
            worker.CreatedUtc, 
            false, 
            null, 
            HashExecutable(worker.Process), worker.Directory));
    }

    /// <summary>
    /// Records that a worker has exited execution on the local host.
    /// </summary>
    /// <param name="worker">The worker execution instance that has exited.</param>
    /// <param name="exitCode">The exit code of the worker process, if available.</param>
    public void RecordExited(WorkerExecution worker, int? exitCode)
        => Append(new LocalHostAgentOwnershipRecord(worker.Launch.InstanceId, worker.Launch.Generation, worker.Process?.Id ?? 0,
            worker.Process?.StartTime.ToUniversalTime() ?? DateTime.UtcNow, DateTimeOffset.UtcNow, true, exitCode));

    /// <summary>
    /// Reads the latest ownership records of workers on the local host.
    /// </summary>
    /// <returns>A read-only list of the latest local host agent ownership records.</returns>
    public IReadOnlyList<LocalHostAgentOwnershipRecord> ReadLatest()
    {
        lock (_sync)
        {
            if (!File.Exists(_journalPath)) 
                return [];
            
            var latest = new Dictionary<string, LocalHostAgentOwnershipRecord>(StringComparer.Ordinal);

            foreach (string line in File.ReadLines(_journalPath))
                if (TryUnprotectJournal(line, out LocalHostAgentOwnershipRecord? record))
                    latest[record!.InstanceId] = record;
            
            return [.. latest.Values];
        }
    }

    /// <summary>
    /// Appends a new local host agent ownership record to the journal file.
    /// </summary>
    /// <param name="record">The local host agent ownership record to append to the journal file.</param>
    private void Append(LocalHostAgentOwnershipRecord record)
    {
        lock (_sync)
        {
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(record, ServiceJson.Options);
            byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

            try
            {
                using var stream = new FileStream(_journalPath, FileMode.Append, FileAccess.Write, FileShare.Read); 
                using var writer = new StreamWriter(stream); 
                writer.WriteLine(Convert.ToBase64String(protectedBytes));
                writer.Flush();
                stream.Flush(true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    /// <summary>
    /// Disposes the resources used by the LocalHostAgentStore.
    /// </summary>
    public void Dispose()
        => _lock.Dispose();

    /// <summary>
    /// Computes the SHA-256 hash of the executable file associated with the given process.
    /// </summary>
    /// <param name="process">The process whose executable file hash is to be computed.</param>
    /// <returns>The SHA-256 hash of the executable file as a hexadecimal string, or null if the hash could not be computed.</returns>
    private static string? HashExecutable(System.Diagnostics.Process process)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(process.MainModule?.FileName ?? throw new InvalidOperationException()))); 
        }
        catch 
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to unprotect and deserialize a journal entry line into a LocalHostAgentOwnershipRecord.
    /// </summary>
    /// <param name="line">The journal entry line to be unprotected and deserialized.</param>
    /// <param name="record">When this method returns, contains the deserialized LocalHostAgentOwnershipRecord if successful; otherwise, null.</param>
    /// <returns>True if the journal entry was successfully unprotected and deserialized; otherwise, false.</returns>
    private static bool TryUnprotectJournal(string line, out LocalHostAgentOwnershipRecord? record)
    { 
        record = null; 
        try
        { 
            byte[] protectedBytes=Convert.FromBase64String(line); 
            byte[] plain=ProtectedData.Unprotect(protectedBytes,null,DataProtectionScope.CurrentUser); 
            try 
            { 
                record=JsonSerializer.Deserialize<LocalHostAgentOwnershipRecord>(plain,ServiceJson.Options); 
                return record is not null; 
            } 
            finally 
            { 
                CryptographicOperations.ZeroMemory(protectedBytes); 
                CryptographicOperations.ZeroMemory(plain); 
            } 
        } 
        catch 
        { 
            return false; 
        } 
    }
}
