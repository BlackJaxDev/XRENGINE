using System.Diagnostics;
using System.Text;
using XREngine.Data;

namespace XREngine.Core.Files;

public partial class TextFile
{
    private readonly object _textLoadGate = new();
    private long _textLoadVersion;
    private bool _textPublicationActive;
    private bool _textLifetimeEnded;

    /// <summary>Reads text through the installed source without scheduling a blocking file job.</summary>
    public Task<bool> LoadTextAsync(string path)
        => LoadTextAsync(path, CancellationToken.None);

    public async Task<bool> LoadTextAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        long version = BeginTextLoad();
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture(cancellationToken);
        bool hasExistence = read.TryGetExists(path, out bool exists);
        if (hasExistence && !exists)
            return false;
        byte[] bytes;
        try { bytes = await read.ReadAllBytesAsync(path).ConfigureAwait(false); }
        catch (IOException error) when (!hasExistence && error is FileNotFoundException or DirectoryNotFoundException)
        {
            read.EnsureCurrent();
            return false;
        }
        PublishText(read, version, bytes);
        return true;
    }

    /// <summary>Reads only when the captured source explicitly supports synchronous bytes.</summary>
    public bool LoadText(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        long version = BeginTextLoad();
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        if (!read.Exists(path))
            return false;
        PublishText(read, version, read.ReadAllBytes(path));
        return true;
    }

    public unsafe void LoadTextFileMapped(string path)
    {
        long version = BeginTextLoad();
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        read.EnsureHostFileAccess("Mapped text read");
        using FileMap map = FileMap.FromFile(path, FileMapProtect.Read);
        PublishText(read, version, new ReadOnlySpan<byte>((byte*)map.Address, checked((int)map.Length)));
    }

    private long BeginTextLoad()
    {
        lock (_textLoadGate)
        {
            ObjectDisposedException.ThrowIf(_textLifetimeEnded || IsDestroyed, this);
            if (_textPublicationActive)
                throw new InvalidOperationException("TextFile.PublicationInProgress: retry loading after the current text publication completes.");
            return ++_textLoadVersion;
        }
    }

    private void PublishText(RuntimeAssetReadLease read, long version, ReadOnlySpan<byte> bytes)
    {
        Encoding encoding = GetEncoding(bytes, out int bomLength);
        string text = encoding.GetString(bytes[bomLength..]);
        using IDisposable publication = read.BeginPublication();
        lock (_textLoadGate)
        {
            if (_textLifetimeEnded || IsDestroyed || version != _textLoadVersion)
                throw new OperationCanceledException("TextFile.StaleRead: the text owner or its requested content changed during the read.");
            if (_textPublicationActive)
                throw new InvalidOperationException("TextFile.PublicationInProgress: another text publication is active.");
            _textPublicationActive = true;
        }
        try
        {
            // Setters may call user code. Neither the source nor object admission gate is held.
            read.EnsureCurrent();
            Encoding = encoding;
            read.EnsureCurrent();
            Text = text;
        }
        finally
        {
            lock (_textLoadGate)
                _textPublicationActive = false;
        }
    }

    public override void Generate()
    {
        bool reviving = IsDestroyed;
        base.Generate();
        if (reviving && !IsDestroyed)
        {
            lock (_textLoadGate)
            {
                _textLifetimeEnded = false;
                ++_textLoadVersion;
            }
        }
    }

    protected override void OnDestroying()
    {
        lock (_textLoadGate)
        {
            if (_textPublicationActive)
                throw new InvalidOperationException("TextFile.PublicationInProgress: retry destruction after the current text publication completes.");
            _textLifetimeEnded = true;
            ++_textLoadVersion;
        }
        base.OnDestroying();
    }

    /// <summary>Detects a BOM using a synchronous source read; asynchronous hosts use LoadTextAsync.</summary>
    public static Encoding GetEncoding(string path)
    {
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        return ReadEncoding(read, path);
    }

    private Encoding ReadFileEncoding(string path)
    {
        long version;
        lock (_textLoadGate)
        {
            ObjectDisposedException.ThrowIf(_textLifetimeEnded || IsDestroyed, this);
            if (_text is not null || _textPublicationActive)
                return _encoding;
            version = _textLoadVersion;
        }
        using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
        Encoding encoding = ReadEncoding(read, path);
        using IDisposable publication = read.BeginPublication();
        lock (_textLoadGate)
        {
            ObjectDisposedException.ThrowIf(_textLifetimeEnded || IsDestroyed, this);
            if (version != _textLoadVersion || _text is not null || _textPublicationActive
                || !string.Equals(FilePath, path, StringComparison.Ordinal))
                return _encoding;
            // This getter historically caches without notifications. The nonnotifying
            // setter performs only a reference check and assignment under this gate.
            SetField(ref _encoding, encoding, publishNotifications: false, nameof(Encoding));
            return _encoding;
        }
    }

    private static Encoding ReadEncoding(RuntimeAssetReadLease read, string path)
    {
        Encoding encoding;
        try
        {
            encoding = GetEncoding(read.ReadAllBytes(path), out _);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Failed to read encoding from file {path}: {error.Message}");
            encoding = Encoding.Default;
        }
        read.EnsureCurrent();
        return encoding;
    }

    public static unsafe Encoding GetEncoding(FileMap file, out int bomLength)
        => GetEncoding(new ReadOnlySpan<byte>((byte*)file.Address, (int)Math.Min(4, file.Length)), out bomLength);

    private static Encoding GetEncoding(ReadOnlySpan<byte> bytes, out int bomLength)
    {
        if (bytes is [0xFF, 0xFE, 0, 0, ..])
        {
            bomLength = 4;
            return Encoding.UTF32;
        }
        if (bytes is [0, 0, 0xFE, 0xFF, ..])
        {
            bomLength = 4;
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        }
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            bomLength = 3;
            return Encoding.UTF8;
        }
        if (bytes is [0xFF, 0xFE, ..])
        {
            bomLength = 2;
            return Encoding.Unicode;
        }
        if (bytes is [0xFE, 0xFF, ..])
        {
            bomLength = 2;
            return Encoding.BigEndianUnicode;
        }
        if (bytes is [0x2B, 0x2F, 0x76, ..])
        {
            bomLength = 3;
#pragma warning disable SYSLIB0001 // Preserve existing UTF-7 source import support.
            return Encoding.UTF7;
#pragma warning restore SYSLIB0001
        }
        bomLength = 0;
        return Encoding.Default;
    }
}
