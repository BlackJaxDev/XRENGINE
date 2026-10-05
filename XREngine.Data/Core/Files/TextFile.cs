using MemoryPack;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using XREngine.Data;
using YamlDotNet.Serialization;

namespace XREngine.Core.Files
{
    /// <summary>
    /// Main class for raw text files.
    /// Overrides default serialization to save the raw text content instead of the object structure.
    /// </summary>
    [XRAssetInspector("XREngine.Editor.AssetEditors.TextFileInspector")]
    [XR3rdPartyExtensions(typeof(XREngine.Data.XRDefault3rdPartyImportOptions), "txt")]
    [MemoryPackable]
    public partial class TextFile : XRAsset
    {
        public event Action? TextChanged;

        private string? _directFilePath;
        [NonSerialized]
        private readonly object _diskProvenanceLock = new();
        [NonSerialized]
        private string? _diskBaselinePath;
        [NonSerialized]
        private string? _diskBaselineText;
        [NonSerialized]
        private long _sourceMutationRevision;
        [NonSerialized]
        private long _refreshRequestRevision;
        [NonSerialized]
        private bool _destroyed;

        [YamlIgnore]
        [JsonIgnore]
        [MemoryPackIgnore]
        public new string? FilePath
        {
            get => _directFilePath ?? base.FilePath;
            set
            {
                string? normalized = NormalizeDirectFilePath(value);
                lock (_diskProvenanceLock)
                {
                    if (!string.Equals(FilePath, normalized, StringComparison.OrdinalIgnoreCase))
                        _sourceMutationRevision++;
                    SetField(ref _directFilePath, normalized);
                    base.FilePath = normalized;
                }
            }
        }

        private string? _text = null;
        public string? Text
        {
            get => _text;
            set
            {
                lock (_diskProvenanceLock)
                {
                    if (!string.Equals(_text, value, StringComparison.Ordinal))
                        _sourceMutationRevision++;
                    SetField(ref _text, value);
                }
            }
        }

        [MemoryPackIgnore]
        private Encoding _encoding = Encoding.Default;
        [YamlIgnore]
        [JsonIgnore]
        [MemoryPackIgnore]
        public Encoding Encoding
        {
            get
            {
                if (_text is null && !string.IsNullOrWhiteSpace(FilePath))
                    _encoding = GetEncoding(FilePath);
                return _encoding;
            }

            set
            {
                lock (_diskProvenanceLock)
                {
                    if (_encoding.CodePage != value.CodePage)
                        _sourceMutationRevision++;
                    SetField(ref _encoding, value);
                }
            }
        }

        public int EncodingCodePage
        {
            get => Encoding.CodePage;
            set => Encoding = Encoding.GetEncoding(value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            switch (propName)
            {
                case nameof(Text):
                    OnTextChanged();
                    break;
            }
        }

        protected void OnTextChanged()
        {
            MarkDirty();
            TextChanged?.Invoke();
        }

        [MemoryPackConstructor]
        public TextFile()
        {
            FilePath = null;
            _text = null;
        }
        public TextFile(string path)
        {
            FilePath = path;
            _text = null;
        }

        public static TextFile FromText(string text)
            => new() { Text = text };

        public static implicit operator string?(TextFile textFile)
            => textFile?.Text;
        public static implicit operator TextFile(string? text)
            => FromText(text ?? string.Empty);

        private static string? NormalizeDirectFilePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        public unsafe void LoadTextFileMapped(string path)
        {
            using FileMap map = FileMap.FromFile(path, FileMapProtect.Read);
            Encoding encoding = GetEncoding(map, out int bomLength);
            ApplyLoadedText(path, encoding.GetString((byte*)map.Address + bomLength, (int)(map.Length - bomLength)), encoding);
        }

        public async Task<bool> LoadTextAsync(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                Encoding encoding = GetEncoding(path);
                string text = await File.ReadAllTextAsync(path, encoding);
                ApplyLoadedText(path, text, encoding);
                return true;
            }
            return false;
        }
        public bool LoadText(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                Encoding encoding = GetEncoding(path);
                ApplyLoadedText(path, File.ReadAllText(path, encoding), encoding);
                return true;
            }
            return false;
        }

        private void ApplyLoadedText(string path, string text, Encoding encoding)
        {
            lock (_diskProvenanceLock)
            {
                Encoding = encoding;
                Text = text;
                _diskBaselinePath = NormalizeDirectFilePath(path);
                _diskBaselineText = text;
                _refreshRequestRevision++;
            }
        }

        /// <summary>
        /// Captures a clean disk-backed source before an off-thread refresh read.
        /// A path assigned to an in-memory source without a successful load or save is not disk-owned.
        /// </summary>
        public bool TryCaptureDiskRefresh(string path, out string baselineText, out long mutationRevision,
            out long requestRevision, out Encoding encoding)
        {
            lock (_diskProvenanceLock)
            {
                baselineText = _diskBaselineText ?? string.Empty;
                mutationRevision = _sourceMutationRevision;
                requestRevision = 0;
                encoding = _encoding;
                string? normalizedPath = NormalizeDirectFilePath(path);
                if (_diskBaselineText is null ||
                    !string.Equals(_diskBaselinePath, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(FilePath, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_text ?? string.Empty, _diskBaselineText, StringComparison.Ordinal) ||
                    _destroyed)
                {
                    return false;
                }

                requestRevision = ++_refreshRequestRevision;
                return true;
            }
        }

        /// <summary>
        /// Applies an off-thread disk read only when neither an edit, path change, nor newer refresh superseded it.
        /// </summary>
        public bool TryApplyDiskRefresh(string path, string baselineText, long mutationRevision,
            long requestRevision, string refreshedText)
        {
            lock (_diskProvenanceLock)
            {
                string? normalizedPath = NormalizeDirectFilePath(path);
                if (_sourceMutationRevision != mutationRevision ||
                    _refreshRequestRevision != requestRevision ||
                    !string.Equals(FilePath, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_diskBaselinePath, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_diskBaselineText, baselineText, StringComparison.Ordinal) ||
                    !string.Equals(_text ?? string.Empty, baselineText, StringComparison.Ordinal) ||
                    _destroyed)
                {
                    return false;
                }

                if (string.Equals(_text ?? string.Empty, refreshedText, StringComparison.Ordinal))
                    return false;

                Text = refreshedText;
                if (!string.Equals(_text, refreshedText, StringComparison.Ordinal))
                    return false;
                _diskBaselineText = refreshedText;
                return true;
            }
        }

        /// <summary>
        /// Determines a text file's encoding by analyzing its byte order mark (BOM).
        /// Defaults to ASCII when detection of the text file's endianness fails.
        /// </summary>
        /// <param name="path">The text file to analyze.</param>
        /// <returns>The detected encoding.</returns>
        public static Encoding GetEncoding(string path)
        {
            try
            {
                byte[] bom = new byte[4];
                //Read the first 4 bytes of the file to check for a BOM
                using (FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4, FileOptions.SequentialScan))
                    fs.ReadExactly(bom, 0, 4);

#pragma warning disable SYSLIB0001 // Type or member is obsolete
                if (bom[0] == 0x2B && bom[1] == 0x2F && bom[2] == 0x76) return Encoding.UTF7;
#pragma warning restore SYSLIB0001 // Type or member is obsolete
                if (bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return Encoding.UTF8;
                if (bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode; //UTF-16LE
                if (bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode; //UTF-16BE
                if (bom[0] == 0 && bom[1] == 0 && bom[2] == 0xFE && bom[3] == 0xFF) return Encoding.UTF32;
                return Encoding.Default;
            }
            catch (Exception e)
            {
                Trace.TraceWarning($"Failed to read encoding from file {path}: {e.Message}");
                return Encoding.Default;
            }
        }
        public static Encoding GetEncoding(FileMap file, out int bomLength)
        {
            byte[] bom = file.Address.GetBytes(4);
            if (bom[0] == 0x2B && bom[1] == 0x2F && bom[2] == 0x76)
            {
                bomLength = 3;
#pragma warning disable SYSLIB0001 // Type or member is obsolete
                return Encoding.UTF7;
#pragma warning restore SYSLIB0001 // Type or member is obsolete
            }
            if (bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            {
                bomLength = 3;
                return Encoding.UTF8;
            }
            if (bom[0] == 0xFF && bom[1] == 0xFE)
            {
                bomLength = 2;
                return Encoding.Unicode; //UTF-16LE
            }
            if (bom[0] == 0xFE && bom[1] == 0xFF)
            {
                bomLength = 2;
                return Encoding.BigEndianUnicode; //UTF-16BE
            }
            if (bom[0] == 0 && bom[1] == 0 && bom[2] == 0xFE && bom[3] == 0xFF)
            {
                bomLength = 4;
                return Encoding.UTF32;
            }

            bomLength = 0;
            return Encoding.Default;
        }

        public override void Reload(string path)
        {
            // Don't reload embedded TextFiles from disk - they live within their parent asset
            if (!ReferenceEquals(SourceAsset, this))
                return;
            
            LoadText(path);
        }
        public override async Task ReloadAsync(string path)
        {
            // Don't reload embedded TextFiles from disk - they live within their parent asset
            if (!ReferenceEquals(SourceAsset, this))
                return;
            
            await LoadTextAsync(path);
        }

        public override bool Load3rdParty(string filePath)
            => LoadText(filePath);
        public override async Task<bool> Load3rdPartyAsync(string filePath)
            => await LoadTextAsync(filePath);

        public override void SerializeTo(string filePath, ISerializer defaultSerializer)
        {
            // Embedded TextFiles should serialize as YAML within their parent
            if (!ReferenceEquals(SourceAsset, this))
            {
                base.SerializeTo(filePath, defaultSerializer);
                return;
            }
            SaveTo(filePath);
        }

        public override Task SerializeToAsync(string filePath, ISerializer defaultSerializer)
        {
            // Embedded TextFiles should serialize as YAML within their parent
            if (!ReferenceEquals(SourceAsset, this))
                return base.SerializeToAsync(filePath, defaultSerializer);
            
            return SaveToAsync(filePath);
        }

        public void SaveTo(string path)
        {
            string text;
            Encoding encoding;
            long revision;
            lock (_diskProvenanceLock)
            {
                text = _text ?? string.Empty;
                encoding = Encoding;
                revision = _sourceMutationRevision;
            }
            File.WriteAllText(path, text, encoding);
            RecordSavedText(path, text, revision);
        }

        public async Task SaveToAsync(string path)
        {
            string text;
            Encoding encoding;
            long revision;
            lock (_diskProvenanceLock)
            {
                text = _text ?? string.Empty;
                encoding = Encoding;
                revision = _sourceMutationRevision;
            }
            await File.WriteAllTextAsync(path, text, encoding);
            RecordSavedText(path, text, revision);
        }

        private void RecordSavedText(string path, string text, long revision)
        {
            lock (_diskProvenanceLock)
            {
                if (_sourceMutationRevision != revision || !string.Equals(_text ?? string.Empty, text, StringComparison.Ordinal))
                    return;
                _diskBaselinePath = NormalizeDirectFilePath(path);
                _diskBaselineText = text;
                _refreshRequestRevision++;
            }
        }

        protected override void OnDestroying()
        {
            lock (_diskProvenanceLock)
                _destroyed = true;
            base.OnDestroying();
        }
    }
}
