using Extensions;
using MemoryPack;
using System;
using System.ComponentModel;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json.Serialization;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine.Core.Files
{
    /// <summary>
    /// An asset is a common base class for all engine-formatted objects that are loaded from disk.
    /// </summary>
    [MemoryPackable(GenerateType.NoGenerate)]
    public abstract partial class XRAsset : XRObjectBase
    {
        [MemoryPackConstructor]
        public XRAsset() { }
        public XRAsset(string name) => Name = name;

        private EventList<XRAsset> _embeddedAssets = [];
        /// <summary>
        /// List of sub-assets contained in this file.
        /// This metadata is automatically reconstructed from the asset graph after loading.
        /// </summary>
        [YamlIgnore]
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
        [Browsable(false)]
        [MemoryPackIgnore]
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

        private DateTime? _originalLastWriteTimeUtc;
        /// <summary>
        /// Timestamp of the source 3rd-party asset when this asset was generated (UTC).
        /// </summary>
        public DateTime? OriginalLastWriteTimeUtc
        {
            get => _originalLastWriteTimeUtc;
            set => SetField(ref _originalLastWriteTimeUtc, value);
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
        [YamlIgnore]
        [Browsable(false)]
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
        [Browsable(false)]
        private MemoryMappedFile? FileMap { get; set; }
        /// <summary>
        /// A stream to the file for sequential reading and writing.
        /// </summary>
        [JsonIgnore]
        [YamlIgnore]
        [Browsable(false)]
        [MemoryPackIgnore]
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

        /// <summary>
        /// Called when importing a 3rd-party file into an engine-native asset. The default
        /// behavior ignores importOptions and defers to <see cref="Load3rdParty(string)"/>.
        /// Override this to apply custom import options.
        /// </summary>
        public virtual bool Import3rdParty(string filePath, object? importOptions)
            => Load3rdParty(filePath);

        public virtual async Task<bool> Load3rdPartyAsync(string filePath)
        {
            //Run the synchronous version of the method async by default
            return await Task.Run(() => Load3rdParty(filePath));
        }

        public virtual async Task<bool> Import3rdPartyAsync(string filePath, object? importOptions)
            => await Task.Run(() => Import3rdParty(filePath, importOptions));

        [Browsable(false)]
        [MemoryPackIgnore]
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
        {
            // Ensure we serialize the *concrete runtime type*.
            // If callers serialize as XRAsset, derived members (e.g., Model meshes/materials) are omitted.
            using var writer = new StreamWriter(filePath, append: false, Encoding.UTF8);
            defaultSerializer.Serialize(writer, this, GetType());
        }

        /// <summary>
        /// This is the main method to serialize the asset to a file using the provided serializer asynchronously.
        /// May be overridden to change the serialization behavior.
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="defaultSerializer"></param>
        /// <returns></returns>
        public virtual async Task SerializeToAsync(string filePath, ISerializer defaultSerializer)
        {
            // Serializer isn't async; serialize first, then write asynchronously.
            string yaml = defaultSerializer.Serialize(this, GetType());
            await File.WriteAllTextAsync(filePath, yaml, Encoding.UTF8).ConfigureAwait(false);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);

            if (propName is nameof(SourceAsset) or nameof(EmbeddedAssets))
                return;

            if (XRAssetGraphUtility.ShouldRefreshForPropertyChange(prev, field))
                XRAssetGraphUtility.RefreshAssetGraph(SourceAsset);
        }
    }

}
