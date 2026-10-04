using MemoryPack;
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

        [YamlIgnore]
        [JsonIgnore]
        [MemoryPackIgnore]
        public new string? FilePath
        {
            get => _directFilePath ?? base.FilePath;
            set
            {
                string? normalized = NormalizeDirectFilePath(value);
                SetField(ref _directFilePath, normalized);
                base.FilePath = normalized;
            }
        }

        private string? _text = null;
        public string? Text
        {
            get => _text;
            set => SetField(ref _text, value);
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
                    return ReadFileEncoding(FilePath);
                return _encoding;
            }

            set => SetField(ref _encoding, value);
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
        public override Task<bool> Load3rdPartyAsync(string filePath)
            => LoadTextAsync(filePath);
        public override Task<bool> Load3rdPartyAsync(string filePath, AssetImportContext context)
            => LoadTextAsync(filePath, context.CancellationToken);
        public override Task<bool> Import3rdPartyAsync(string filePath, object? importOptions)
            => LoadTextAsync(filePath);

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
            RuntimeAssetReadServices.EnsureHostFileAccess("Text file save");
            File.WriteAllText(path, _text ?? string.Empty, Encoding);
        }

        public async Task SaveToAsync(string path)
        {
            RuntimeAssetReadServices.EnsureHostFileAccess("Text file save");
            await File.WriteAllTextAsync(path, _text ?? string.Empty, Encoding).ConfigureAwait(false);
        }
    }
}
