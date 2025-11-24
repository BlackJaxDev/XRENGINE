using Extensions;
using System.IO.MemoryMappedFiles;
using System.Text.Json.Serialization;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine.Core.Files
{
    /// <summary>
    /// An asset is a common base class for all engine-formatted objects that are loaded from disk.
    /// </summary>
    public abstract partial class XRAsset : XRObjectBase
    {
        public XRAsset() { }
        public XRAsset(string name) => Name = name;

        private EventList<XRAsset> _embeddedAssets = [];
        /// <summary>
        /// List of sub-assets contained in this file.
        /// </summary>
        public EventList<XRAsset> EmbeddedAssets
        {
            get => _embeddedAssets; 
            internal set => SetField(ref _embeddedAssets, value);
        }

        private string? _serializedAssetType;

        /// <summary>
        /// Serialized type hint injected into YAML files so tools can determine the asset's concrete type without full deserialization.
        /// </summary>
        [YamlMember(Alias = "__assetType", Order = -100)]
        public string SerializedAssetType
        {
            get => GetType().FullName ?? GetType().Name;
            private set => _serializedAssetType = value;
        }

        private string? _originalPath;
        /// <summary>
        /// The original path of this asset before it was imported and converted for engine use.
        /// </summary>
        public string? OriginalPath
        {
            get => _originalPath;
            set => SetField(ref _originalPath, value);
        }

        private string? _filePath;
        /// <summary>
        /// The absolute origin of this asset in the file system.
        /// </summary>
        [YamlIgnore]
        public string? FilePath
        {
            get => _sourceAsset is null || _sourceAsset == this ? _filePath : _sourceAsset.FilePath;
            set => SetField(ref _filePath, (value?.IsValidPath() ?? false) ? Path.GetFullPath(value) : value);
        }

        private XRAsset? _sourceAsset = null;
        private bool _isDirty = false;

        /// <summary>
        /// The root asset that this asset resides inside of.
        /// The root asset is the one actually written as a file instead of being included in another asset.
        /// </summary>
        public XRAsset SourceAsset
        {
            get => _sourceAsset ?? this;
            set => SetField(ref _sourceAsset, value);
        }

        /// <summary>
        /// The map of the asset in memory for unsafe pointer use.
        /// </summary>
        [JsonIgnore]
        [YamlIgnore]
        private MemoryMappedFile? FileMap { get; set; }
        /// <summary>
        /// A stream to the file for sequential reading and writing.
        /// </summary>
        [JsonIgnore]
        [YamlIgnore]
        public MemoryMappedViewStream? FileMapStream { get; private set; }

        public void OpenForStreaming()
        {
            if (FilePath is null)
                throw new InvalidOperationException("Cannot open a file for streaming without a file path.");

            CloseStreaming();

            FileMap = MemoryMappedFile.CreateFromFile(FilePath);
            FileMapStream = FileMap.CreateViewStream();
        }
        public void CloseStreaming()
        {
            FileMapStream?.Dispose();
            FileMapStream = null;

            FileMap?.Dispose();
            FileMap = null;
        }

        /// <summary>
        /// Called when the filePath has an extension that is in the XR3rdPartyExtensionsAttribute list.
        /// </summary>
        /// <param name="filePath"></param>
        public virtual bool Load3rdParty(string filePath)
        {
            return false;
        }

        public virtual async Task<bool> Load3rdPartyAsync(string filePath)
        {
            //Run the synchronous version of the method async by default
            return await Task.Run(() => Load3rdParty(filePath));
        }

        public bool IsDirty
        {
            get => _isDirty;
            private set => SetField(ref _isDirty, value);
        }

        public void MarkDirty()
        {
            IsDirty = true;
        }
        public void ClearDirty()
        {
            IsDirty = false;
        }

        public event Action<XRAsset>? Reloaded;

        /// <summary>
        /// Reloads the asset from its file path.
        /// </summary>
        public void Reload()
        {
            if (FilePath is null)
                return;

            Reload(FilePath);
            Reloaded?.Invoke(this);
        }

        /// <summary>
        /// Reloads the asset from a file path.
        /// </summary>
        /// <param name="path"></param>
        public virtual void Reload(string path)
        {

        }

        /// <summary>
        /// Reloads the asset from its file path asynchronously.
        /// </summary>
        /// <returns></returns>
        public async Task ReloadAsync()
        {
            if (FilePath is null)
                return;

            await ReloadAsync(FilePath);
            Reloaded?.Invoke(this);
        }

        /// <summary>
        /// Reloads the asset from a file path asynchronously.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public virtual async Task ReloadAsync(string path)
            => await Task.Run(() => Reload(path));

        /// <summary>
        /// This is the main method to serialize the asset to a file using the provided serializer.
        /// May be overridden to change the serialization behavior.
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="defaultSerializer"></param>
        public virtual void SerializeTo(string filePath, ISerializer defaultSerializer)
            => File.WriteAllText(filePath, defaultSerializer.Serialize(this));

        /// <summary>
        /// This is the main method to serialize the asset to a file using the provided serializer asynchronously.
        /// May be overridden to change the serialization behavior.
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="defaultSerializer"></param>
        /// <returns></returns>
        public virtual async Task SerializeToAsync(string filePath, ISerializer defaultSerializer)
            => await File.WriteAllTextAsync(filePath, defaultSerializer.Serialize(this));
    }
}
